// Program.cs — .NET Framework 4.7.2/4.8, C# 7.3
// References: Ifs.Fnd.Core.dll, Ifs.Fnd.Data.dll, Ifs.Fnd.AccessProvider.dll,
// Ifs.Fnd.AccessProvider.Interactive.dll, Ifs.Fnd.Diagnostics.dll, System.Windows.Forms
// Additionally: System.Web.Extensions (JavaScriptSerializer)
// Optional: SharpCompress (NuGet) – to extract .7z (fallback to 7z.exe)

using Ifs.Fnd.AccessProvider;
using Ifs.Fnd.AccessProvider.Interactive;
using Ifs.Fnd.AccessProvider.PLSQL;
using Ifs.Fnd.Data;
using Ifs.Fnd.Diagnostics;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using System.Security.Cryptography;
using System.Web.Script.Serialization; // System.Web.Extensions

namespace IfsIfsDialogStyle
{
    internal static class Program
    {
        // ===== IFS CONFIG =====
        private const string serviceUser = "";   // "login" (leave empty => IFS Login Dialog)
        private const string servicePass = "";   // "SuperSecret!"
        private const bool enableIfsDebug = false;

        // Debug output for MF flat file / whitelist
        private const bool DebugFlat = true;

        private static readonly Dictionary<string, string> EnvMap =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "prod", "http://ifsappserver:59080" },
                { "test", "http://ifsappserver:60080" },
                { "demo", "http://ifsappserver:59080" },
            };

        // ===== FLAT FILE STATE =====
        private static HashSet<string> FlatIndex = new HashSet<string>(StringComparer.Ordinal);
        private static List<string> FlatMasks = new List<string>();   // masks from MF JSON (if any)
        private static int FlatTransformCount = 1;                    // header.liczbaTransformacji (e.g., 5000)
        private static string FlatIndexDate;                          // yyyy-MM-dd (info only)

        // ===== MF FLAT FILE CONFIG =====
        // Local cache for downloaded archives and extracted data
        private static readonly string CacheRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "mf_flat_cache");
        // Path to 7z.exe (fallback when not using SharpCompress)
        private const string SevenZipExePath = @"C:\Program Files\7-Zip\7z.exe";
        // Flat file URL pattern
        private const string FlatUrlPattern = "https://plikplaski.mf.gov.pl/pliki//{0}.7z"; // {0}=yyyyMMdd

        [STAThread]
        private static int Main(string[] args)
        {
            // ===== Strict parameter validation =====
            // Expected: <environment> <company<=20> <proposal_id:int>
            if (args.Length < 3)
            {
                Console.Error.WriteLine("Usage: MykBialaListaEmisja.exe <environment> <company<=20> <proposal_id:int>");
                Console.Error.WriteLine("Allowed environments: " + string.Join(", ", EnvMap.Keys));
                return 1;
            }

            string envKey = args[0]?.Trim();
            string company = args[1]?.Trim();
            string proposalIdStr = args[2]?.Trim();

            // 1) Environment must exist in EnvMap
            string envUrl;
            try { envUrl = ResolveEnvUrlStrict(envKey); }
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine("Environment error: " + ex.Message);
                return 2;
            }

            // 2) Company must be non-empty and <= 20 characters
            if (string.IsNullOrWhiteSpace(company))
            {
                Console.Error.WriteLine("Error: company is required.");
                return 3;
            }
            if (company.Length > 20)
            {
                Console.Error.WriteLine("Error: company length exceeds 20 characters.");
                return 3;
            }

            // 3) Proposal_id must be an integer
            if (!int.TryParse(proposalIdStr, out int proposalId))
            {
                Console.Error.WriteLine("Error: proposal_id must be an integer.");
                return 4;
            }

            // ===== Prepare MF flat index for today =====
            try
            {

                // Clean up archived flat-file caches (keep only today's folder if present)
                CleanupOldFlatCache(keepToday: true);

                // Prepare today's MF flat index
                EnsureFlatIndexForToday();
                Console.WriteLine($"Flat file: index for {FlatIndexDate} loaded. Entries: {FlatIndex.Count:N0}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Flat file error: " + ex.Message);
                return 5;
            }

            // ===== Open IFS session and run logic =====
            try
            {
                var conn = new FndConnection { ConnectionString = envUrl };

                if (enableIfsDebug)
                {
                    conn.DebugSettings.DebugMode = true;
                    var ds = new FndDebugSettings { ServerTraceEnabled = true };
                    FndDebugMonitor.StartDebugMonitor(ds);
                }

                // 1) Service mode without UI
                if (!string.IsNullOrWhiteSpace(serviceUser) && !string.IsNullOrWhiteSpace(servicePass))
                {
                    conn.InteractiveMode = false;
                    conn.AutoLogon = false;

                    var u = serviceUser;
                    if (u.IndexOf('\\') < 0 && u.IndexOf('@') < 0) u = "DOMAIN\\" + u;

                    conn.SetCredentials(u, servicePass);
                    conn.OpenDedicatedSession();

                    RunBusinessLogic(conn, company, proposalId.ToString());

                    conn.CloseDedicatedSession(true);
                    return 0;
                }

                // 2) Interactive IFS Login Dialog
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                conn.InteractiveMode = true;
                conn.AutoLogon = true;

                var lc = FndLoginDialog.GetStoredCredentials();
                lc.ShowOptions = false;
                var dlg = new FndLoginDialog(conn);
                dlg.ShowDialog(lc, false);
                if (lc == null) return 6;
                FndLoginDialog.StoreCredentials(lc);

                if (lc.UserName != null && lc.Password != null)
                {
                    string identity = !string.IsNullOrEmpty(lc.Identity) ? lc.Identity : lc.UserName;
                    conn.SetCredentials(identity, lc.Password);
                }

                conn.OpenDedicatedSession();

                RunBusinessLogic(conn, company, proposalId.ToString());

                conn.CloseDedicatedSession(true);
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Runtime error: " + ex.Message);
                return 99;
            }
        }

        // ----------------- BUSINESS LOGIC -----------------
        private static void RunBusinessLogic(FndConnection conn, string company, string proposalId)
        {
            var rows = SelectPolishProposalItems(conn, company, proposalId);
            Console.WriteLine($"Rows found: {rows.Count}");

            // Use today's date from file name (cache) for CF fields and hashing
            string todayIso = DateTime.Today.ToString("yyyy-MM-dd");
            string todayYmd = DateTime.Today.ToString("yyyyMMdd");

            int okCnt = 0, errCnt = 0;

            foreach (var r in rows)
            {
                string nip = NormalizeDigits(r.TaxIdNumber);
                // Bank account: use ADDRESS_ID, keep digits only
                string nrb = NormalizeDigits(r.AddressId);

                // If NRB length is not 26 – treat as "no account"
                if (!string.IsNullOrEmpty(nrb) && nrb.Length != 26)
                    nrb = null;

                if (string.IsNullOrEmpty(nip))
                {
                    Console.WriteLine($"[SKIP] Missing NIP for OBJID={r.Objid}");
                    continue;
                }

                // Check MF flat index (only if NRB exists)
                var chk = CheckFlatAssigned(nip, nrb);  // (hit, usedHash, usedInput)
                bool assigned = chk.hit;

                // Hash actually used to get the hit (5000 iterations + optional mask).
                // If no hit or no NRB – MF allows hash of (date+NIP) only.
                string mfHash = chk.usedHash ?? Sha512HexIter(todayYmd + nip, FlatTransformCount);

                if (DebugFlat)
                {
                    string dbgInput = chk.usedInput ?? (todayYmd + nip);
                    // Console.WriteLine($"[CHK] NIP={nip} NRB={(nrb ?? "-")}  input={dbgInput}  iter={FlatTransformCount}  hash={mfHash.Substring(0, Math.Min(16, mfHash.Length))}  HIT={assigned}");
                }

                string whitelistResult = assigned ? "FIGURUJE" : "NIE FIGURUJE";

                // Unit/Record separators required by IFS CF parameters
                string us = "\u001F";  // 0x1F
                string rs = "\u001E";  // 0x1E

                // CF parameters: C — named-value pairs separated by RS, with US between name and value
                string cParam =
                    $"CF$_BIALALISTADATA{us}{todayIso}|{mfHash}{rs}" +
                    $"CF$_BIALALISTAODP{us}{whitelistResult}{rs}";

                // D param must not be null (use empty string)
                string dParam = "";

                var (ok, msg) = CfModifyWithRetry(conn, r.Objid, cParam, dParam, "DO", maxAttempts: 5, delayMs: 2000);
                if (ok) okCnt++; else errCnt++;
                Console.WriteLine($"{(ok ? "[OK]" : "[ERR]")} OBJID={r.Objid} WL={whitelistResult} HASH={mfHash}  {msg}");
            }

            Console.WriteLine($"Total: OK={okCnt}, ERR={errCnt}");
        }

        // ----------------- IFS SELECT -----------------
        private static List<ProposalRow> SelectPolishProposalItems(FndConnection conn, string company, string proposalId)
        {
            var list = new List<ProposalRow>();

            const string sql = @"
                SELECT
                    A.COMPANY,
                    A.PROPOSAL_ID,
                    A.PROPOSAL_ITEM_ID,
                    A.IDENTITY,
                    A.ADDRESS_ID,
                    A.TAX_ID_NUMBER,
                    IFSAPP.SUPPLIER_INFO_API.Get_Country(A.IDENTITY) AS SUPPLIER_COUNTRY,
                    A.OBJID,
                    A.OBJVERSION
                FROM IFSAPP.PROPOSAL_LEDGER_ITEM_CFV A
                WHERE IFSAPP.SUPPLIER_INFO_API.Get_Country(A.IDENTITY) = 'POLAND'
                  AND A.COMPANY      = :P_COMPANY
                  AND A.PROPOSAL_ID  = :P_PROPOSAL_ID
                ORDER BY A.PROPOSAL_ITEM_ID";

            var cmd = new FndPLSQLSelectCommand(conn, sql);
            cmd.BindVariables.Add(new FndBindVariable(FndBindVariableDirection.In, "P_COMPANY", new FndTextAttribute(company)));
            cmd.BindVariables.Add(new FndBindVariable(FndBindVariableDirection.In, "P_PROPOSAL_ID", new FndTextAttribute(proposalId)));

            FndDataTable tbl = cmd.ExecuteReader();
            foreach (FndDataRow row in tbl)
            {
                list.Add(new ProposalRow
                {
                    Company = Convert.ToString(row["COMPANY"]),
                    ProposalId = Convert.ToString(row["PROPOSAL_ID"]),
                    ProposalItemId = Convert.ToString(row["PROPOSAL_ITEM_ID"]),
                    Identity = Convert.ToString(row["IDENTITY"]),
                    AddressId = Convert.ToString(row["ADDRESS_ID"]),
                    TaxIdNumber = Convert.ToString(row["TAX_ID_NUMBER"]),
                    SupplierCountry = Convert.ToString(row["SUPPLIER_COUNTRY"]),
                    BankAccountNo = Convert.ToString(row["ADDRESS_ID"]),
                    Objid = Convert.ToString(row["OBJID"]),
                    Objversion = Convert.ToString(row["OBJVERSION"])
                });
            }

            return list;
        }

        // ----------------- IFS CF MODIFY -----------------
        private static Tuple<bool, string> CfModifyWithRetry(
            FndConnection conn, string objid, string cParam, string dParam, string eParam,
            int maxAttempts = 5, int delayMs = 2000)
        {
            const string plsql = @"
        DECLARE
          a_ VARCHAR2(32000) := NULL;
          b_ VARCHAR2(32000) := :P_B;
          c_ VARCHAR2(32000) := :P_C;
          d_ VARCHAR2(32000) := :P_D;
          e_ VARCHAR2(32000) := :P_E;
        BEGIN
          IFSAPP.PROPOSAL_LEDGER_ITEM_CFP.Cf_Modify__(a_, b_, c_, d_, e_);
          COMMIT;
        END;";

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                var cmd = new FndPLSQLSelectCommand(conn, plsql);
                cmd.BindVariables.Add(new FndBindVariable(FndBindVariableDirection.In, "P_B", new FndTextAttribute(objid)));
                cmd.BindVariables.Add(new FndBindVariable(FndBindVariableDirection.In, "P_C", new FndTextAttribute(cParam)));
                cmd.BindVariables.Add(new FndBindVariable(FndBindVariableDirection.In, "P_D", new FndTextAttribute(dParam ?? "")));
                cmd.BindVariables.Add(new FndBindVariable(FndBindVariableDirection.In, "P_E", new FndTextAttribute(eParam ?? "")));

                // For troubleshooting: see the exact C parameter string
                Console.WriteLine(cParam);

                try
                {
                    cmd.ExecuteNonQuery();
                    return Tuple.Create(true, $"Cf_Modify__ OK (attempt {attempt})");
                }
                catch (Exception ex)
                {
                    string msg = ex.Message ?? "";

                    // Typical object locks: IFS message + ORA-00054
                    bool isLock =
                        msg.IndexOf("locked by another user", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        msg.IndexOf("ORA-00054", StringComparison.OrdinalIgnoreCase) >= 0;

                    if (!isLock || attempt == maxAttempts)
                        return Tuple.Create(false, msg);

                    // Wait and retry
                    System.Threading.Thread.Sleep(delayMs);
                }
            }

            return Tuple.Create(false, "Failed after all attempts (lock).");
        }

        // ----------------- HASHING -----------------
        private static string Sha512Hex(string input)
        {
            using (var sha = SHA512.Create())
            {
                var bytes = Encoding.UTF8.GetBytes(input);
                var hash = sha.ComputeHash(bytes);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (var b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        // Repeat SHA-512 N times (MF transform count)
        private static string Sha512HexIter(string input, int iterations)
        {
            if (iterations <= 1) return Sha512Hex(input);
            string cur = input;
            for (int i = 0; i < iterations; i++)
                cur = Sha512Hex(cur);
            return cur;
        }

        // ----------------- MF FLAT FILE: DOWNLOAD / EXTRACT / INDEX -----------------
        private static void EnsureFlatIndexForToday()
        {
            string todayYmd = DateTime.Today.ToString("yyyyMMdd");
            string todayDir = Path.Combine(CacheRoot, todayYmd);
            string marker = Path.Combine(todayDir, ".ready");

            if (File.Exists(marker))
            {
                // Already have today's index
                FlatIndex = LoadOrBuildIndex(todayDir);
                FlatIndexDate = DateTime.Today.ToString("yyyy-MM-dd");
                return;
            }

            // Purge old cache and prepare a fresh folder
            if (Directory.Exists(CacheRoot))
            {
                try { Directory.Delete(CacheRoot, true); } catch { /* ignore */ }
            }
            Directory.CreateDirectory(todayDir);

            // Download archive
            string url = string.Format(FlatUrlPattern, todayYmd);
            string archivePath = Path.Combine(todayDir, $"{todayYmd}.7z");
            Console.WriteLine("Downloading MF flat file: " + url);
            DownloadFile(url, archivePath);

            // Extract
            Console.WriteLine("Extracting .7z …");
            Extract7z(archivePath, todayDir);

            // Build index
            FlatIndex = LoadOrBuildIndex(todayDir);

            // Ready marker
            File.WriteAllText(marker, "ok");
            FlatIndexDate = DateTime.Today.ToString("yyyy-MM-dd");
        }

        private static void DownloadFile(string url, string destPath)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            using (var http = new HttpClient())
            {
                http.Timeout = TimeSpan.FromMinutes(5);
                var resp = http.GetAsync(url).GetAwaiter().GetResult();
                if (!resp.IsSuccessStatusCode)
                {
                    var body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    throw new Exception($"HTTP {(int)resp.StatusCode} while downloading flat file: {body}");
                }
                var bytes = resp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                File.WriteAllBytes(destPath, bytes);
            }
        }

        private static void Extract7z(string archivePath, string extractDir)
        {
            // 1) Try SharpCompress (if referenced); otherwise fallback to 7z.exe.
            bool extracted = false;
            try
            {
                var asm = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name.Equals("SharpCompress", StringComparison.OrdinalIgnoreCase));
                if (asm != null)
                {
                    ExtractWithSharpCompress(archivePath, extractDir, asm);
                    extracted = true;
                }
            }
            catch { /* fallback to 7z.exe */ }

            if (!extracted)
            {
                if (!File.Exists(SevenZipExePath))
                    throw new Exception("SharpCompress not found and 7z.exe missing. Install 7-Zip or add SharpCompress.");

                var psi = new ProcessStartInfo
                {
                    FileName = SevenZipExePath,
                    Arguments = $"x \"{archivePath}\" -o\"{extractDir}\" -y",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (var p = Process.Start(psi))
                {
                    string so = p.StandardOutput.ReadToEnd();
                    string se = p.StandardError.ReadToEnd();
                    p.WaitForExit();
                    if (p.ExitCode != 0)
                        throw new Exception($"7z.exe returned {p.ExitCode}. STDOUT:\n{so}\nSTDERR:\n{se}");
                }
            }
        }

        // Minimal extraction via reflection (if SharpCompress is available)
        private static void ExtractWithSharpCompress(string archivePath, string extractDir, System.Reflection.Assembly sharpAsm)
        {
            var archiveType = sharpAsm.GetType("SharpCompress.Archives.SevenZip.SevenZipArchive");
            var openMethod = archiveType.GetMethod("Open", new[] { typeof(Stream), sharpAsm.GetType("SharpCompress.Readers.ReaderOptions") });
            var readerOptionsType = sharpAsm.GetType("SharpCompress.Readers.ReaderOptions");
            var passwordProp = readerOptionsType.GetProperty("Password");
            var options = Activator.CreateInstance(readerOptionsType);
            passwordProp?.SetValue(options, null, null);

            using (FileStream fs = File.OpenRead(archivePath))
            using (IDisposable archive = (IDisposable)openMethod.Invoke(null, new object[] { fs, options }))
            {
                var entriesProp = archiveType.GetProperty("Entries");
                var entries = (System.Collections.IEnumerable)entriesProp.GetValue(archive);
                foreach (var entry in entries)
                {
                    var entryType = entry.GetType();
                    bool isDir = (bool)entryType.GetProperty("IsDirectory").GetValue(entry);
                    if (isDir) continue;
                    var writeToDirMethod = entryType.GetMethod("WriteToDirectory", new[] { typeof(string), sharpAsm.GetType("SharpCompress.Common.ExtractionOptions") });
                    var extrOptionsType = sharpAsm.GetType("SharpCompress.Common.ExtractionOptions");
                    var extrOptions = Activator.CreateInstance(extrOptionsType);
                    writeToDirMethod.Invoke(entry, new object[] { extractDir, extrOptions });
                }
            }
        }

        private static HashSet<string> LoadOrBuildIndex(string dir)
        {
            var idxPath = Path.Combine(dir, "index.txt");
            var metaPath = Path.Combine(dir, "index.meta");
            var masksPath = Path.Combine(dir, "masks.txt");

            if (File.Exists(idxPath))
            {
                var hs = new HashSet<string>(StringComparer.Ordinal);
                foreach (var line in File.ReadAllLines(idxPath))
                    if (!string.IsNullOrWhiteSpace(line)) hs.Add(line.Trim());

                if (File.Exists(metaPath))
                {
                    if (int.TryParse(File.ReadAllText(metaPath).Trim(), out int n) && n > 0)
                        FlatTransformCount = n;
                }
                FlatMasks = File.Exists(masksPath)
                    ? File.ReadAllLines(masksPath).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList()
                    : new List<string>();

                if (DebugFlat)
                    Console.WriteLine($"[FLAT] index.txt loaded: {hs.Count:N0} hashes, Transform={FlatTransformCount}, Masks={FlatMasks.Count}");

                return hs;
            }

            var ymd = DateTime.Today.ToString("yyyyMMdd");
            var jsonPath = Path.Combine(dir, $"{ymd}.json");
            if (!File.Exists(jsonPath))
                throw new Exception($"Missing {ymd}.json in {dir}.");

            var json = File.ReadAllText(jsonPath, Encoding.UTF8);
            var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            var root = ser.Deserialize<Dictionary<string, object>>(json);

            // transform count
            FlatTransformCount = 1;
            try
            {
                var hdr = root["naglowek"] as Dictionary<string, object>;
                if (int.TryParse(Convert.ToString(hdr?["liczbaTransformacji"]), out int n) && n > 0)
                    FlatTransformCount = n;
                FlatIndexDate = Convert.ToString(hdr?["dataGenerowaniaDanych"]);
            }
            catch { /* leave as 1 */ }

            // hashes – support ArrayList/IEnumerable
            var hsNew = new HashSet<string>(StringComparer.Ordinal);

            void AddArr(object obj)
            {
                if (obj == null) return;
                var en = obj as System.Collections.IEnumerable;
                if (en == null) return;

                int added = 0;
                foreach (var o in en)
                {
                    var hex = Convert.ToString(o)?.Trim();
                    if (!string.IsNullOrEmpty(hex) && hsNew.Add(hex)) added++;
                }
                if (DebugFlat)
                    Console.WriteLine($"[FLAT] added hashes: {added:N0}");
            }

            if (root.ContainsKey("skrotyPodatnikowCzynnych")) AddArr(root["skrotyPodatnikowCzynnych"]);
            if (root.ContainsKey("skrotyPodatnikowZwolnionych")) AddArr(root["skrotyPodatnikowZwolnionych"]);

            // masks (MF may publish with various names)
            FlatMasks = new List<string>();
            foreach (var key in new[] { "maski", "maskiRachunkowWirtualnych", "maskiRachunkiWirtualne" })
            {
                if (!root.ContainsKey(key)) continue;
                var en = root[key] as System.Collections.IEnumerable;
                if (en == null) continue;
                foreach (var o in en)
                {
                    var s = Convert.ToString(o)?.Trim();
                    if (!string.IsNullOrEmpty(s) && s.Length == 26 && s.All(ch => char.IsDigit(ch) || ch == 'X' || ch == 'Y'))
                        FlatMasks.Add(s);
                }
            }

            if (DebugFlat)
                Console.WriteLine($"[FLAT] JSON built: {hsNew.Count:N0} hashes, Transform={FlatTransformCount}, Masks={FlatMasks.Count}");

            File.WriteAllLines(idxPath, hsNew);
            File.WriteAllText(metaPath, FlatTransformCount.ToString());
            if (FlatMasks.Any()) File.WriteAllLines(masksPath, FlatMasks);

            return hsNew;
        }

        // Return (hit, usedHash, usedInput) instead of bool
        private static (bool hit, string usedHash, string usedInput) CheckFlatAssigned(string nip, string nrb)
        {
            if (FlatIndex == null) return (false, null, null);
            string ymd = DateTime.Today.ToString("yyyyMMdd");

            // 1) Try without mask
            if (!string.IsNullOrEmpty(nrb) && nrb.Length == 26)
            {
                string input = ymd + nip + nrb;
                string h = Sha512HexIter(input, FlatTransformCount);
                if (FlatIndex.Contains(h))
                    return (true, h, input);
            }

            // 2) Try with masks
            if (string.IsNullOrEmpty(nrb) || nrb.Length != 26 || FlatMasks == null || FlatMasks.Count == 0)
                return (false, null, null);

            string bank8 = nrb.Substring(2, 8);
            foreach (var mask in FlatMasks)
            {
                if (mask.Length != 26) continue;

                // Ensure bank segment (8 digits) matches
                bool bankOk = true;
                for (int i = 0; i < 8; i++)
                {
                    char m = mask[2 + i];
                    if (char.IsDigit(m) && m != bank8[i]) { bankOk = false; break; }
                }
                if (!bankOk) continue;

                var sb = new StringBuilder(26);
                for (int pos = 0; pos < 26; pos++)
                {
                    char m = mask[pos];
                    if (m == 'Y') sb.Append(nrb[pos]);
                    else if (m == 'X') sb.Append('X');
                    else sb.Append(m);
                }
                string masked = sb.ToString();
                string input = ymd + nip + masked;
                string h = Sha512HexIter(input, FlatTransformCount);
                if (FlatIndex.Contains(h))
                    return (true, h, input);
            }

            return (false, null, null);
        }
        // Deletes all cache subfolders except today's (or whole cache if keepToday = false).
        private static void CleanupOldFlatCache(bool keepToday = true)
        {
            try
            {
                if (!Directory.Exists(CacheRoot)) return;

                string todayYmd = DateTime.Today.ToString("yyyyMMdd");
                foreach (var dir in Directory.EnumerateDirectories(CacheRoot))
                {
                    var name = Path.GetFileName(dir);
                    bool isToday = string.Equals(name, todayYmd, StringComparison.OrdinalIgnoreCase);
                    if (keepToday && isToday) continue;

                    try { Directory.Delete(dir, true); } catch { /* ignore */ }
                }

                // also remove stray files at root
                foreach (var file in Directory.EnumerateFiles(CacheRoot))
                {
                    try { File.Delete(file); } catch { /* ignore */ }
                }
            }
            catch { /* ignore */ }
        }

        // ----------------- HELPERS -----------------
        private static string ResolveEnvUrlStrict(string key)
        {
            if (EnvMap.TryGetValue(key?.Trim() ?? "", out var url)) return url;
            throw new ArgumentException($"Unknown environment: {key}. Allowed: {string.Join(", ", EnvMap.Keys)}");
        }

        private static string NormalizeDigits(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            var digits = Regex.Replace(s, @"\D", "");
            return digits.Length == 0 ? null : digits;
        }

        // ----------------- MODEL -----------------
        private sealed class ProposalRow
        {
            public string Company { get; set; }
            public string ProposalId { get; set; }
            public string ProposalItemId { get; set; }
            public string Identity { get; set; }
            public string AddressId { get; set; }
            public string TaxIdNumber { get; set; }
            public string SupplierCountry { get; set; }
            public string BankAccountNo { get; set; } // alias of ADDRESS_ID
            public string Objid { get; set; }
            public string Objversion { get; set; }
        }
    }
}
