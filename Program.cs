using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using System.Data.SqlClient;
using System.Runtime.InteropServices;

namespace XiahPatchGateway
{
    class Program
    {
        private static readonly string PatchDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "patch_dir");
        private static int Port = 8080;

        private const string ROUTE_PATCH_CONFIG = "patch_config";
        private const string INI_SECTION_PATCH = "Patch";
        private const string INI_SECTION_NETWORK = "Network";
        private const string INI_KEY_DOWNLOAD_BASE_URL = "DownloadBaseUrl";

        // 内存缓存的最新的 JSON 清单字符串，降低玩家请求时的 IO 压力
        private static string CurrentManifestJson = "";

        // MD5 缓存，结构：相对路径 -> (最后修改时间, 文件大小, MD5)
        private static readonly Dictionary<string, CacheEntry> Md5Cache = new Dictionary<string, CacheEntry>();
        private static readonly object CacheLock = new object();

        struct IpBanRule
        {
            public string Target;
            public string Reason;
        }

        private static readonly object BanCacheLock = new object();
        private static DateTime BanCacheExpiresAtUtc = DateTime.MinValue;
        private static List<IpBanRule> CachedIpBanRules = new List<IpBanRule>();

        struct CacheEntry
        {
            public DateTime LastWriteTime;
            public long Size;
            public string Md5;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int nStdHandle);

        [DllImport("kernel32.dll")]
        private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

        [DllImport("kernel32.dll")]
        private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

        private const int STD_INPUT_HANDLE = -10;
        private const uint ENABLE_QUICK_EDIT_MODE = 0x0040;
        private const uint ENABLE_EXTENDED_FLAGS = 0x0080;

        /// <summary>
        /// 彻底禁用 Windows 控制台快速编辑模式 (QuickEdit Mode)
        /// 防止鼠标无意在控制台黑窗中单击选中文字导致整个网关进程被内核挂起、阻断所有玩家更新请求
        /// </summary>
        private static void DisableConsoleQuickEdit()
        {
            try
            {
                IntPtr handle = GetStdHandle(STD_INPUT_HANDLE);
                uint mode;
                if (GetConsoleMode(handle, out mode))
                {
                    mode &= ~ENABLE_QUICK_EDIT_MODE;
                    mode |= ENABLE_EXTENDED_FLAGS;
                    SetConsoleMode(handle, mode);
                }
            }
            catch { }
        }

        static void Main(string[] args)
        {
            Console.Title = "Xiah Oriental Fantasy 资源网关服务";
            DisableConsoleQuickEdit();
            
            // 兼容 Windows 8.1 / 7 / Server 经典控制台，使用系统默认编码（GBK/CP936），避免 UTF-8 下点阵字体显示为希腊字母乱码
            try
            {
                Console.OutputEncoding = Encoding.Default;
                Console.InputEncoding = Encoding.Default;
            }
            catch { }

            // 动态解析端口，如果本地同级有 xiah.ini，自动读取 PatchUrl 里的端口
            Port = GetPortFromIni();

            if (!Directory.Exists(PatchDir))
            {
                Directory.CreateDirectory(PatchDir);
            }

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("============================================================");
            Console.WriteLine("        Xiah Oriental Fantasy 资源网关服务 (EXE 独立运行版)");
            Console.WriteLine("============================================================");
            Console.ResetColor();
            Console.WriteLine("[物理路径] 补丁根目录 : " + PatchDir);
            Console.WriteLine("[网关监听] 外部请求IP : http://*:" + Port + "/");
            string configuredBaseUrl = GetDownloadBaseUrlFromIni();
            Console.WriteLine("[补丁分发] 下载根路径 : " + (string.IsNullOrEmpty(configuredBaseUrl) ? "未配置外部源，由本机 patch_dir 直接分发" : configuredBaseUrl));
            Console.WriteLine("[提示说明] 1. 只需要将客户端更新文件按原目录层级放入 patch_dir 中即可。");
            Console.WriteLine("[提示说明] 2. 服务端每次启动都会在 patch_dir 下自动生成物理静态 manifest.json");
            Console.WriteLine("[提示说明]    这意味您甚至可以直接使用 Nginx 或 IIS 指向 patch_dir 来提供更新！");
            Console.WriteLine("------------------------------------------------------------");

            // 执行冷扫描，预先载入并输出物理静态 manifest.json 到磁盘
            CurrentManifestJson = ScanAndGenerateManifest();
            Console.WriteLine("[系统状态] 首次冷扫描校验完毕，已自动在 patch_dir 下写入最新的静态 manifest.json");

            // 启动后台定时重扫线程 (每 60 秒自动重扫一次，完全异步，且复用 MD5 高性能缓存)
            Task.Run(async () =>
            {
                while (true)
                {
                    await Task.Delay(60000); // 延时 60 秒
                    try
                    {
                        string newManifest = ScanAndGenerateManifest();
                        lock (CacheLock)
                        {
                            CurrentManifestJson = newManifest;
                        }
                        Log(ConsoleColor.Yellow, "动态同步", "后台定时重扫完成，已自动刷新清单与物理 manifest.json 文件！");
                    }
                    catch (Exception ex)
                    {
                        Log(ConsoleColor.Red, "系统错误", "后台定时重扫发生异常: " + ex.Message);
                    }
                }
            });

            HttpListener listener = new HttpListener();
            try
            {
                try
                {
                    listener.Prefixes.Add("http://*:" + Port + "/");
                    listener.Start();
                }
                catch (HttpListenerException ex)
                {
                    if (ex.ErrorCode == 5)
                    {
                        Log(ConsoleColor.Yellow, "网关自适应", "检测到系统权限受限，已自动为您安全回退至本地环回测试监听模式 (127.0.0.1)！");
                        listener = new HttpListener();
                        listener.Prefixes.Add("http://127.0.0.1:" + Port + "/");
                        listener.Start();
                    }
                    else
                    {
                        throw;
                    }
                }

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("[网络监听] 服务启动成功！网关正在等待玩家连接...\n");
                Console.ResetColor();

                // 启动异步网络请求分发线程
                Task.Run(async () =>
                {
                    while (listener.IsListening)
                    {
                        try
                        {
                            HttpListenerContext context = await listener.GetContextAsync();
                            // 使用单独的任务处理，防止大文件传输阻塞其他玩家的握手
                            Task.Run(() => HandleClientRequest(context));
                        }
                        catch
                        {
                            // 忽略服务关闭造成的异常
                        }
                    }
                });

                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("--> [按 Enter 键可以安全关闭此资源网关]");
                Console.ResetColor();
                Console.ReadLine();
                
                listener.Stop();
                Console.WriteLine("[系统状态] 网关已安全停止。");
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("\n[致命错误] 服务启动失败！如果是由于 Windows 系统端口权限受限，请“右键使用管理员身份运行”此程序。\n原因: " + ex.Message);
                Console.ResetColor();
                Console.ReadLine();
            }
        }

        /// <summary>
        /// 核心请求分发处理器
        /// </summary>
        private static void HandleClientRequest(HttpListenerContext context)
        {
            HttpListenerRequest request = context.Request;
            HttpListenerResponse response = context.Response;

            // 获取玩家连接的 IP 地址
            string clientIp = request.RemoteEndPoint.Address.ToString();
            string rawUrl = request.Url.AbsolutePath;

            // 统一路径规范
            string requestedPath = Uri.UnescapeDataString(rawUrl).TrimStart('/');

            // 0. 处理账号注册请求
            if (requestedPath.Equals("register", StringComparison.OrdinalIgnoreCase) || requestedPath.EndsWith("/register", StringComparison.OrdinalIgnoreCase))
            {
                HandleRegisterRequest(context);
                return;
            }

            // 0.5 处理账号查重请求（注册弹窗焦点离开时的实时检测）
            if (requestedPath.Equals("check_account", StringComparison.OrdinalIgnoreCase) || requestedPath.EndsWith("/check_account", StringComparison.OrdinalIgnoreCase))
            {
                HandleCheckAccountRequest(context);
                return;
            }

            // 0.8 下发客户端补丁下载根路径配置（仅返回 baseUrl，严禁下发防盗链 Referer）
            if (requestedPath.Equals(ROUTE_PATCH_CONFIG, StringComparison.OrdinalIgnoreCase) ||
                requestedPath.EndsWith("/" + ROUTE_PATCH_CONFIG, StringComparison.OrdinalIgnoreCase))
            {
                HandlePatchConfigRequest(context);
                return;
            }

            // 1. 处理动态清单请求 manifest.json (采用 EndsWith 支持带 /patch/ 等任意前缀的请求)
            if (requestedPath.EndsWith("manifest.json", StringComparison.OrdinalIgnoreCase) || requestedPath.EndsWith("/manifest", StringComparison.OrdinalIgnoreCase) || requestedPath.Equals("manifest", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    Log(ConsoleColor.Green, "清单请求", "IP: " + clientIp + " 正在请求比对哈希清单...");

                    string manifestJson;
                    lock (CacheLock)
                    {
                        manifestJson = CurrentManifestJson;
                    }
                    byte[] buffer = Encoding.UTF8.GetBytes(manifestJson);

                    response.ContentType = "application/json; charset=utf-8";
                    response.ContentLength64 = buffer.Length;
                    
                    // 开启 CORS 跨域，支持 Web 端或第三方工具访问
                    response.AddHeader("Access-Control-Allow-Origin", "*");
                    response.OutputStream.Write(buffer, 0, buffer.Length);
                }
                catch (Exception ex)
                {
                    Log(ConsoleColor.Red, "系统错误", "向 IP: " + clientIp + " 分发清单失败: " + ex.Message);
                    response.StatusCode = (int)HttpStatusCode.InternalServerError;
                }
                finally
                {
                    response.Close();
                }
                return;
            }

            // 2. 静态补丁文件传输分发
            // 防止目录穿越漏洞 (安全校验)
            if (requestedPath.Contains(".."))
            {
                Log(ConsoleColor.Red, "安全拦截", "IP: " + clientIp + " 尝试目录穿越非法访问: " + requestedPath);
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                response.Close();
                return;
            }

            string localFilePath = Path.Combine(PatchDir, requestedPath.Replace('/', Path.DirectorySeparatorChar));

            if (!File.Exists(localFilePath))
            {
                Log(ConsoleColor.DarkYellow, "未找到文件", "IP: " + clientIp + " 请求的资源不存在: " + requestedPath);
                response.StatusCode = (int)HttpStatusCode.NotFound;
                response.Close();
                return;
            }

            try
            {
                Log(ConsoleColor.Cyan, "下载传送", string.Format("IP: {0} 正在下载资源 -> {1} ({2} KB)", clientIp, requestedPath, new FileInfo(localFilePath).Length / 1024));

                using (FileStream fs = File.OpenRead(localFilePath))
                {
                    response.ContentType = "application/octet-stream";
                    response.ContentLength64 = fs.Length;
                    
                    byte[] buffer = new byte[65536]; // 64KB 高速缓冲区
                    int bytesRead;
                    while ((bytesRead = fs.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        response.OutputStream.Write(buffer, 0, bytesRead);
                    }
                }
            }
            catch (Exception ex)
            {
                Log(ConsoleColor.Red, "传输中断", string.Format("IP: {0} 下载资源 {1} 被强行中断: {2}", clientIp, requestedPath, ex.Message));
            }
            finally
            {
                try { response.Close(); } catch { }
            }
        }

        /// <summary>
        /// 扫描 Patch 目录并构建 JSON 字符串 (带高性能 mtime 缓存，并自动生成物理 manifest.json)
        /// </summary>
        private static string ScanAndGenerateManifest()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("[\n");

            var fileList = new List<string>();
            GetFilesRecursive(PatchDir, fileList);

            List<string> jsonObjects = new List<string>();

            foreach (var filepath in fileList)
            {
                try
                {
                    string relPath = filepath.Substring(PatchDir.Length).TrimStart(Path.DirectorySeparatorChar).Replace('\\', '/');
                    string fileName = Path.GetFileName(relPath);
                    
                    // 1. 过滤清单文件本身、崩溃转储与运行日志、激活态的 d3d9.dll，以及玩家本地个人配置文件 (xiah.ini / cheat*.ini / launcher*)
                    //    (保留 config.ini 等游戏数据配置正常下发；xiah.ini 若本地缺失会由客户端首次启动自动生成标准基础配置)
                    if (relPath.Equals("manifest.json", StringComparison.OrdinalIgnoreCase) ||
                        fileName.Equals("d3d9.dll", StringComparison.OrdinalIgnoreCase) ||
                        fileName.Equals("xiah.ini", StringComparison.OrdinalIgnoreCase) ||
                        fileName.Equals("cheat.ini", StringComparison.OrdinalIgnoreCase) ||
                        (fileName.StartsWith("cheat_", StringComparison.OrdinalIgnoreCase) && fileName.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)) ||
                        fileName.Equals("launcher.ini", StringComparison.OrdinalIgnoreCase) ||
                        fileName.Equals("launcher_accounts.dat", StringComparison.OrdinalIgnoreCase) ||
                        fileName.Equals("Xiah.log", StringComparison.OrdinalIgnoreCase) ||
                        fileName.Equals("Xiah_Close.log", StringComparison.OrdinalIgnoreCase) ||
                        fileName.Equals("Xiah_TEST.log", StringComparison.OrdinalIgnoreCase) ||
                        relPath.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase) ||
                        relPath.EndsWith("_d3d9.log", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    FileInfo fi = new FileInfo(filepath);
                    DateTime mtime = fi.LastWriteTime;
                    long size = fi.Length;

                    string fileMd5 = "";

                    lock (CacheLock)
                    {
                        CacheEntry cached;
                        if (Md5Cache.TryGetValue(relPath, out cached) && cached.LastWriteTime == mtime && cached.Size == size)
                        {
                            fileMd5 = cached.Md5;
                        }
                        else
                        {
                            fileMd5 = CalculateMD5(filepath);
                            if (!string.IsNullOrEmpty(fileMd5))
                            {
                                Md5Cache[relPath] = new CacheEntry
                                {
                                    LastWriteTime = mtime,
                                    Size = size,
                                    Md5 = fileMd5
                                };
                            }
                        }
                    }

                    if (!string.IsNullOrEmpty(fileMd5))
                    {
                        // 构造符合 C# 正则匹配器解析的 JSON 对象
                        jsonObjects.Add(string.Format("  {{ \"path\": \"{0}\", \"md5\": \"{1}\", \"size\": {2} }}", relPath, fileMd5, size));
                    }
                }
                catch { }
            }

            sb.Append(string.Join(",\n", jsonObjects));
            sb.Append("\n]");
            string json = sb.ToString();

            // 自动将生成的 JSON 写入物理静态文件，完美契合标准 Nginx / IIS 静态发布！
            try
            {
                string staticManifestPath = Path.Combine(PatchDir, "manifest.json");
                File.WriteAllText(staticManifestPath, json, Encoding.UTF8);
            }
            catch { }

            return json;
        }

        private static void GetFilesRecursive(string sDir, List<string> fileList)
        {
            try
            {
                foreach (string f in Directory.GetFiles(sDir))
                {
                    fileList.Add(f);
                }
                foreach (string d in Directory.GetDirectories(sDir))
                {
                    GetFilesRecursive(d, fileList);
                }
            }
            catch { }
        }

        private static string CalculateMD5(string filename)
        {
            using (var md5 = MD5.Create())
            {
                try
                {
                    using (var stream = new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        var hash = md5.ComputeHash(stream);
                        StringBuilder sb = new StringBuilder();
                        for (int i = 0; i < hash.Length; i++)
                        {
                            sb.Append(hash[i].ToString("x2"));
                        }
                        return sb.ToString();
                    }
                }
                catch (Exception ex)
                {
                    Log(ConsoleColor.DarkYellow, "哈希告警", string.Format("文件 {0} 暂时无法读取计算指纹 (可能正在写入或被独占): {1}", Path.GetFileName(filename), ex.Message));
                    return "";
                }
            }
        }

        /// <summary>
        /// 解析服务端 config.ini 物理路径（优先同级目录，其次回退上级目录以兼容多级输出结构）
        /// </summary>
        private static string ResolveConfigIniPath()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string configPath = Path.Combine(baseDir, "config.ini");
            if (File.Exists(configPath))
            {
                return configPath;
            }

            // 去除末尾分隔符后再取父目录，避免 .NET GetParent 返回自身目录路径
            string trimmedBase = baseDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            DirectoryInfo parentDir = Directory.GetParent(trimmedBase);
            if (parentDir != null)
            {
                string parentConfig = Path.Combine(parentDir.FullName, "config.ini");
                if (File.Exists(parentConfig))
                {
                    return parentConfig;
                }
            }

            return configPath;
        }

        /// <summary>
        /// 剥离 INI 配置项中的行内分号注释及首尾引号
        /// </summary>
        private static string StripIniValueCommentAndQuotes(string rawValue)
        {
            if (string.IsNullOrEmpty(rawValue)) return "";
            string val = rawValue.Trim();
            int commentIdx = val.IndexOf(';');
            if (commentIdx >= 0)
            {
                val = val.Substring(0, commentIdx).Trim();
            }
            return val.Trim('"', '\'');
        }

        /// <summary>
        /// 实时读取 config.ini 中的 [Patch] DownloadBaseUrl 配置，支持运维修改后免重启即时生效
        /// </summary>
        private static string GetDownloadBaseUrlFromIni()
        {
            string configPath = ResolveConfigIniPath();
            if (!File.Exists(configPath)) return "";

            try
            {
                string currentSection = "";
                foreach (string rawLine in File.ReadAllLines(configPath, Encoding.UTF8))
                {
                    string trimmed = rawLine.Trim();
                    if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith(";")) continue;

                    if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
                    {
                        currentSection = trimmed.Substring(1, trimmed.Length - 2).Trim();
                        continue;
                    }

                    bool isTargetSection = currentSection.Equals(INI_SECTION_PATCH, StringComparison.OrdinalIgnoreCase) ||
                                           currentSection.Equals(INI_SECTION_NETWORK, StringComparison.OrdinalIgnoreCase);
                    if (!isTargetSection) continue;

                    var parts = trimmed.Split(new[] { '=' }, 2);
                    if (parts.Length != 2 || !parts[0].Trim().Equals(INI_KEY_DOWNLOAD_BASE_URL, StringComparison.OrdinalIgnoreCase)) continue;

                    string val = StripIniValueCommentAndQuotes(parts[1]);
                    if (val.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                        val.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    {
                        return val.EndsWith("/") ? val : (val + "/");
                    }
                    return "";
                }
            }
            catch (Exception ex)
            {
                Log(ConsoleColor.Yellow, "配置读取", "解析 DownloadBaseUrl 失败: " + ex.Message);
            }
            return "";
        }

        /// <summary>
        /// 处理客户端补丁根路径配置下发请求（GET /patch_config）
        /// 安全约束：仅下发下载根路径，严禁在此接口中返回任何防盗链 Referer 凭据
        /// </summary>
        private static void HandlePatchConfigRequest(HttpListenerContext context)
        {
            HttpListenerResponse response = context.Response;
            string clientIp = context.Request.RemoteEndPoint.Address.ToString();
            response.AddHeader("Access-Control-Allow-Origin", "*");

            try
            {
                string baseUrl = GetDownloadBaseUrlFromIni();
                string safeUrl = baseUrl.Replace("\\", "\\\\").Replace("\"", "\\\"");
                string json = string.Format("{{\"baseUrl\":\"{0}\"}}", safeUrl);
                Log(ConsoleColor.Green, "配置下发", string.Format("IP: {0} 获取补丁根路径 -> {1}", clientIp, string.IsNullOrEmpty(baseUrl) ? "(本机直连)" : baseUrl));
                SendJsonResponse(response, (int)HttpStatusCode.OK, json);
            }
            catch (Exception ex)
            {
                Log(ConsoleColor.Red, "配置异常", string.Format("IP: {0} 读取补丁配置失败: {1}", clientIp, ex.Message));
                SendJsonResponse(response, (int)HttpStatusCode.InternalServerError, "{\"baseUrl\":\"\"}");
            }
        }

        private static int GetPortFromIni()
        {
            // 1. 优先读取服务端的核心配置文件 config.ini
            string configPath = ResolveConfigIniPath();
            if (File.Exists(configPath))
            {
                try
                {
                    string[] lines = File.ReadAllLines(configPath);
                    foreach (string line in lines)
                    {
                        string trimmed = line.Trim();
                        // 支持直接配置端口：PatchPort=9005
                        if (trimmed.StartsWith("PatchPort", StringComparison.OrdinalIgnoreCase))
                        {
                            var parts = trimmed.Split(new[] { '=' }, 2);
                            if (parts.Length == 2)
                            {
                                int p;
                                if (int.TryParse(parts[1].Trim(), out p))
                                {
                                    return p;
                                }
                            }
                        }
                        // 也支持从完整 URL 中解析：PatchUrl=http://...:9005/
                        else if (trimmed.StartsWith("PatchUrl", StringComparison.OrdinalIgnoreCase))
                        {
                            var parts = trimmed.Split(new[] { '=' }, 2);
                            if (parts.Length == 2)
                            {
                                string url = parts[1].Trim();
                                Uri uri = new Uri(url);
                                return uri.Port;
                            }
                        }
                    }
                }
                catch { }
            }

            // 2. 次优先读取 xiah.ini 配置文件
            string iniPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "xiah.ini");
            if (File.Exists(iniPath))
            {
                try
                {
                    string[] lines = File.ReadAllLines(iniPath);
                    foreach (string line in lines)
                    {
                        string trimmed = line.Trim();
                        if (trimmed.StartsWith("PatchUrl", StringComparison.OrdinalIgnoreCase))
                        {
                            var parts = trimmed.Split(new[] { '=' }, 2);
                            if (parts.Length == 2)
                            {
                                string url = parts[1].Trim();
                                Uri uri = new Uri(url);
                                return uri.Port;
                            }
                        }
                    }
                }
                catch { }
            }

            return 8080; // 默认端口
        }

        private static void Log(ConsoleColor color, string tag, string message)
        {
            lock (Console.Out)
            {
                string time = DateTime.Now.ToString("HH:mm:ss");
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write("[" + time + "] ");
                
                Console.ForegroundColor = color;
                Console.Write("[" + tag + "] ");
                
                Console.ForegroundColor = ConsoleColor.Gray;
                Console.WriteLine(message);
                Console.ResetColor();
            }
        }

        /// <summary>
        /// 处理账号查重请求（GET /check_account?username=xxx）
        /// 用于注册弹窗中账号输入框失焦时的实时查重反馈
        /// </summary>
        private static void HandleCheckAccountRequest(HttpListenerContext context)
        {
            HttpListenerResponse response = context.Response;
            string clientIp = context.Request.RemoteEndPoint.Address.ToString();

            response.AddHeader("Access-Control-Allow-Origin", "*");

            string username = context.Request.QueryString["username"] ?? "";
            username = username.Trim();

            if (string.IsNullOrEmpty(username) || username.Length < 4 || username.Length > 12 || !Regex.IsMatch(username, "^[a-zA-Z0-9]+$"))
            {
                SendJsonResponse(response, (int)HttpStatusCode.BadRequest, "{\"exists\":false,\"message\":\"参数无效\"}");
                return;
            }

            try
            {
                string connStr = GetDbConnectionString();
                bool exists = false;

                try
                {
                    using (var conn = new SqlConnection(connStr))
                    {
                        conn.Open();
                        string sql = "SELECT COUNT(1) FROM Account WHERE szAccount = @Account";
                        using (var cmd = new SqlCommand(sql, conn))
                        {
                            cmd.Parameters.AddWithValue("@Account", username);
                            exists = Convert.ToInt32(cmd.ExecuteScalar()) > 0;
                        }
                    }
                }
                catch
                {
                    // 数据库不可达时降级到 Mock 文件检测
                    string mockDbPath = Path.Combine(PatchDir, "mock_accounts.txt");
                    if (File.Exists(mockDbPath))
                    {
                        string[] lines = File.ReadAllLines(mockDbPath);
                        foreach (string line in lines)
                        {
                            var parts = line.Split(':');
                            if (parts.Length >= 1 && parts[0].Trim().Equals(username, StringComparison.OrdinalIgnoreCase))
                            {
                                exists = true;
                                break;
                            }
                        }
                    }
                }

                string json = string.Format("{{\"exists\":{0}}}", exists ? "true" : "false");
                SendJsonResponse(response, (int)HttpStatusCode.OK, json);
            }
            catch (Exception ex)
            {
                Log(ConsoleColor.Red, "查重异常", string.Format("IP: {0} 查重失败: {1}", clientIp, ex.Message));
                SendJsonResponse(response, (int)HttpStatusCode.InternalServerError, "{\"exists\":false,\"message\":\"查询失败\"}");
            }
        }

        /// <summary>
        /// 处理账号注册请求的核心分发入口 (符合 40 行内圈复杂度控制)
        /// </summary>
        private static void HandleRegisterRequest(HttpListenerContext context)
        {
            HttpListenerRequest request = context.Request;
            HttpListenerResponse response = context.Response;
            string clientIp = request.RemoteEndPoint.Address.ToString();

            // 设置跨域响应头
            response.AddHeader("Access-Control-Allow-Origin", "*");
            response.AddHeader("Access-Control-Allow-Methods", "POST, OPTIONS");
            response.AddHeader("Access-Control-Allow-Headers", "Content-Type");

            if (request.HttpMethod.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
            {
                response.StatusCode = (int)HttpStatusCode.OK;
                response.Close();
                return;
            }

            if (!request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                Log(ConsoleColor.Yellow, "注册拦截", "IP: " + clientIp + " 尝试使用非 POST 请求注册");
                response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                response.Close();
                return;
            }

            ProcessRegisterPayload(request, response, clientIp);
        }

        /// <summary>
        /// 解析与处理注册有效载荷
        /// </summary>
        private static void ProcessRegisterPayload(HttpListenerRequest request, HttpListenerResponse response, string clientIp)
        {
            try
            {
                string requestBody;
                using (var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8))
                {
                    requestBody = reader.ReadToEnd();
                }

                string username = "";
                string password = "";
                string email = "";
                ParseCredentials(requestBody, out username, out password, out email);

                string errorMsg;
                if (!ValidateRegisterInput(username, password, email, out errorMsg))
                {
                    SendJsonResponse(response, (int)HttpStatusCode.BadRequest, string.Format("{{\"success\":false,\"message\":\"{0}\"}}", errorMsg));
                    return;
                }

                string connStr = GetDbConnectionString();
                string banReason;
                if (IsIpBannedForRegister(connStr, clientIp, out banReason))
                {
                    Log(ConsoleColor.Red, "注册拦截", string.Format("拒绝封禁 IP [{0}] 注册请求{1}", clientIp, string.IsNullOrEmpty(banReason) ? "" : ("，原因: " + banReason)));
                    SendJsonResponse(response, 403, "{\"success\":false,\"message\":\"当前网络IP已被限制注册账号\"}");
                    return;
                }

                Log(ConsoleColor.Green, "注册请求", string.Format("IP: {0} 尝试注册账号: {1} 邮箱: {2}", clientIp, username, email));

                if (DbRegisterAccount(connStr, username, password, email, out errorMsg))
                {
                    Log(ConsoleColor.Green, "注册成功", string.Format("IP: {0} 账号 {1} 注册成功！", clientIp, username));
                    SendJsonResponse(response, (int)HttpStatusCode.OK, "{\"success\":true,\"message\":\"注册成功，快去登录吧！\"}");
                }
                else
                {
                    Log(ConsoleColor.Red, "注册失败", string.Format("IP: {0} 注册失败: {1}", clientIp, errorMsg));
                    SendJsonResponse(response, (int)HttpStatusCode.BadRequest, string.Format("{{\"success\":false,\"message\":\"{0}\"}}", errorMsg));
                }
            }
            catch (Exception ex)
            {
                Log(ConsoleColor.Red, "注册异常", string.Format("IP: {0} 发生故障: {1}", clientIp, ex.Message));
                SendJsonResponse(response, (int)HttpStatusCode.InternalServerError, string.Format("{{\"success\":false,\"message\":\"网关内部错误: {0}\"}}", ex.Message.Replace("\"", "\\\"")));
            }
        }

        /// <summary>
        /// 解析报文中的账号密码
        /// </summary>
        private static void ParseCredentials(string body, out string username, out string password, out string email)
        {
            username = "";
            password = "";
            email = "";
            if (body.Trim().StartsWith("{"))
            {
                var userMatch = Regex.Match(body, @"""username""\s*:\s*""(?<val>[^""]+)""");
                var passMatch = Regex.Match(body, @"""password""\s*:\s*""(?<val>[^""]+)""");
                var emailMatch = Regex.Match(body, @"""email""\s*:\s*""(?<val>[^""]+)""");
                if (userMatch.Success) username = userMatch.Groups["val"].Value;
                if (passMatch.Success) password = passMatch.Groups["val"].Value;
                if (emailMatch.Success) email = emailMatch.Groups["val"].Value;
            }
            else
            {
                var parts = body.Split('&');
                foreach (var p in parts)
                {
                    var kv = p.Split('=');
                    if (kv.Length == 2)
                    {
                        string key = Uri.UnescapeDataString(kv[0]).Trim();
                        string val = Uri.UnescapeDataString(kv[1]).Trim();
                        if (key.Equals("username", StringComparison.OrdinalIgnoreCase)) username = val;
                        else if (key.Equals("password", StringComparison.OrdinalIgnoreCase)) password = val;
                        else if (key.Equals("email", StringComparison.OrdinalIgnoreCase)) email = val;
                    }
                }
            }
            username = username.Trim();
            password = password.Trim();
            email = email.Trim();
        }

        /// <summary>
        /// 校验账号密码的格式（限制长度 4-12，仅英文数字）
        /// </summary>
        private static bool ValidateRegisterInput(string username, string password, string email, out string errorMsg)
        {
            errorMsg = "";
            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
            {
                errorMsg = "账号和密码不能为空";
                return false;
            }
            if (username.Length < 4 || username.Length > 12 || !Regex.IsMatch(username, "^[a-zA-Z0-9]+$"))
            {
                errorMsg = "账号必须为4-12位英文或数字";
                return false;
            }
            if (password.Length < 4 || password.Length > 12 || !Regex.IsMatch(password, "^[a-zA-Z0-9]+$"))
            {
                errorMsg = "密码必须为4-12位英文或数字";
                return false;
            }
            if (string.IsNullOrEmpty(email) || !Regex.IsMatch(email, @"^\S+@\S+\.\S+$"))
            {
                errorMsg = "请输入有效的安全邮箱地址";
                return false;
            }
            if (email.Length > 128)
            {
                errorMsg = "邮箱地址长度不能超过128个字符";
                return false;
            }
            return true;
        }

        /// <summary>
        /// 写入 SQL Server 数据库逻辑 (带离线 Mock 退避与降级保护，参数化防注入)
        /// </summary>
        private static bool DbRegisterAccount(string connStr, string username, string password, string email, out string errorMsg)
        {
            errorMsg = "";
            try
            {
                using (var conn = new SqlConnection(connStr))
                {
                    conn.Open();
                    string checkSql = "SELECT COUNT(1) FROM Account WHERE szAccount = @Account";
                    using (var cmd = new SqlCommand(checkSql, conn))
                    {
                        cmd.Parameters.AddWithValue("@Account", username);
                        int exists = Convert.ToInt32(cmd.ExecuteScalar());
                        if (exists > 0)
                        {
                            errorMsg = "该账号已存在，请换一个名称";
                            return false;
                        }
                    }
                    // 注册写入账号、密码、用户昵称和安全邮箱
                    // szUserName 为 NOT NULL 字段，用账号名作为默认昵称
                    string insertSql = "INSERT INTO Account (szAccount, szPasswd, szUserName, szEmail) VALUES (@Account, @Passwd, @UserName, @Email)";
                    using (var cmd = new SqlCommand(insertSql, conn))
                    {
                        cmd.Parameters.AddWithValue("@Account", username);
                        cmd.Parameters.AddWithValue("@Passwd", password);
                        cmd.Parameters.AddWithValue("@UserName", username);
                        cmd.Parameters.AddWithValue("@Email", email);
                        cmd.ExecuteNonQuery();
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                Log(ConsoleColor.Yellow, "网关容错", "数据库无法连接 (" + ex.Message + ")。正在为您自动激活本地模拟降级机制...");
                try
                {
                    string mockDbPath = Path.Combine(PatchDir, "mock_accounts.txt");
                    if (!Directory.Exists(PatchDir)) Directory.CreateDirectory(PatchDir);

                    if (File.Exists(mockDbPath))
                    {
                        string[] lines = File.ReadAllLines(mockDbPath);
                        foreach (string line in lines)
                        {
                            var parts = line.Split(':');
                            if (parts.Length >= 1 && parts[0].Trim().Equals(username, StringComparison.OrdinalIgnoreCase))
                            {
                                errorMsg = "该账号已存在(Mock)，请换一个名称";
                                return false;
                            }
                        }
                    }

                    // Mock 降级路径同步写入邮箱信息
                    File.AppendAllText(mockDbPath, string.Format("{0}:{1}:{2}\n", username, password, email));
                    Log(ConsoleColor.Cyan, "本地降级", "已成功将账号离线写入 mock_accounts.txt 中供离线调试！");
                    return true;
                }
                catch (Exception mockEx)
                {
                    errorMsg = "离线模拟降级写入也发生故障: " + mockEx.Message;
                    return false;
                }
            }
        }

        /// <summary>
        /// 动态读取 config.ini 获取 SQL Server 数据库连接字符串
        /// </summary>
        private static string GetDbConnectionString()
        {
            string configPath = ResolveConfigIniPath();

            string server = "127.0.0.1";
            string user = "sa";
            string pass = "Ahtd!@123";
            string db = "xiah_account";

            if (File.Exists(configPath))
            {
                try
                {
                    string[] lines = File.ReadAllLines(configPath);
                    string currentSection = "";
                    foreach (string line in lines)
                    {
                        string trimmed = line.Trim();
                        if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith(";")) continue;
                        if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
                        {
                            currentSection = trimmed.Substring(1, trimmed.Length - 2).Trim();
                            continue;
                        }
                        if (currentSection.Equals("Database", StringComparison.OrdinalIgnoreCase))
                        {
                            var parts = trimmed.Split(new[] { '=' }, 2);
                            if (parts.Length == 2)
                            {
                                string key = parts[0].Trim();
                                string val = parts[1].Trim();
                                if (key.Equals("ServerName", StringComparison.OrdinalIgnoreCase)) server = val;
                                else if (key.Equals("UserID", StringComparison.OrdinalIgnoreCase)) user = val;
                                else if (key.Equals("Password", StringComparison.OrdinalIgnoreCase)) pass = val;
                                else if (key.Equals("AccountDB", StringComparison.OrdinalIgnoreCase)) db = val;
                            }
                        }
                    }
                }
                catch { }
            }
            return string.Format("Server={0};Database={1};User ID={2};Password={3};Connect Timeout=5;", server, db, user, pass);
        }

        /// <summary>
        /// 发送 JSON 响应
        /// </summary>
        private static void SendJsonResponse(HttpListenerResponse response, int statusCode, string json)
        {
            try
            {
                byte[] buffer = Encoding.UTF8.GetBytes(json);
                response.StatusCode = statusCode;
                response.ContentType = "application/json; charset=utf-8";
                response.ContentLength64 = buffer.Length;
                response.OutputStream.Write(buffer, 0, buffer.Length);
            }
            catch { }
            finally
            {
                try { response.Close(); } catch { }
            }
        }

        private static string NormalizeIpAddress(string rawIp)
        {
            if (string.IsNullOrWhiteSpace(rawIp)) return "";
            string ip = rawIp.Trim();
            if (ip.StartsWith("::ffff:", StringComparison.OrdinalIgnoreCase))
                ip = ip.Substring(7);
            return ip;
        }

        private static bool IsLoopbackIp(string ip)
        {
            if (string.IsNullOrEmpty(ip)) return true;
            if (ip == "::1" || ip.Equals("localhost", StringComparison.OrdinalIgnoreCase) || ip == "0.0.0.0")
                return true;
            if (ip.StartsWith("127.", StringComparison.Ordinal))
                return true;
            return false;
        }

        private static bool IsIpRuleMatch(string ip, string ruleTarget)
        {
            if (string.IsNullOrEmpty(ip) || string.IsNullOrEmpty(ruleTarget)) return false;
            string rule = ruleTarget.Trim();
            int starIdx = rule.IndexOf('*');
            if (starIdx >= 0)
            {
                string prefix = rule.Substring(0, starIdx);
                return !string.IsNullOrEmpty(prefix) && ip.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
            }
            return string.Equals(ip, rule, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsIpBannedForRegister(string connStr, string clientIp, out string banReason)
        {
            banReason = "";
            string ip = NormalizeIpAddress(clientIp);
            if (IsLoopbackIp(ip)) return false;

            List<IpBanRule> rules = null;
            DateTime nowUtc = DateTime.UtcNow;
            bool needRefresh = false;
            lock (BanCacheLock)
            {
                if (nowUtc < BanCacheExpiresAtUtc)
                {
                    rules = CachedIpBanRules;
                }
                else
                {
                    needRefresh = true;
                }
            }

            if (needRefresh)
            {
                var freshRules = new List<IpBanRule>();
                try
                {
                    using (var conn = new SqlConnection(connStr))
                    {
                        conn.Open();
                        const string sql =
                            "IF OBJECT_ID('dbo.SECURITY_BAN_LIST', 'U') IS NOT NULL " +
                            "SELECT szTarget, ISNULL(szReason, '') " +
                            "FROM dbo.SECURITY_BAN_LIST WITH (NOLOCK) " +
                            "WHERE bActive = 1 AND bBanType = 2 " +
                            "AND (dtExpireTime IS NULL OR dtExpireTime > GETDATE())";
                        using (var cmd = new SqlCommand(sql, conn))
                        {
                            cmd.CommandTimeout = 3;
                            using (var reader = cmd.ExecuteReader())
                            {
                                while (reader.Read())
                                {
                                    string target = reader.IsDBNull(0) ? "" : reader.GetString(0).Trim();
                                    string reason = reader.IsDBNull(1) ? "" : reader.GetString(1).Trim();
                                    if (!string.IsNullOrEmpty(target))
                                    {
                                        freshRules.Add(new IpBanRule { Target = target, Reason = reason });
                                    }
                                }
                            }
                        }
                    }

                    lock (BanCacheLock)
                    {
                        CachedIpBanRules = freshRules;
                        BanCacheExpiresAtUtc = DateTime.UtcNow.AddSeconds(10);
                        rules = CachedIpBanRules;
                    }
                }
                catch (Exception ex)
                {
                    Log(ConsoleColor.Yellow, "封禁缓存", "查询 IP 封禁表失败，放行本次校验: " + ex.Message);
                    lock (BanCacheLock)
                    {
                        BanCacheExpiresAtUtc = DateTime.UtcNow.AddSeconds(5);
                        rules = CachedIpBanRules;
                    }
                }
            }

            if (rules != null)
            {
                foreach (var rule in rules)
                {
                    if (IsIpRuleMatch(ip, rule.Target))
                    {
                        banReason = rule.Reason;
                        return true;
                    }
                }
            }
            return false;
        }
    }
}
