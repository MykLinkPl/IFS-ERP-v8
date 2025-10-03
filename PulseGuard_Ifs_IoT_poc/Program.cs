// -- Check IoT values
// -- Oracle 11g compatible
// -- Author: Przemysław Myk — MykLink / Smart Connections
// -- https://github.com/MykLinkPl

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Windows.Forms;
using Ifs.Fnd.AccessProvider;
using Ifs.Fnd.AccessProvider.Interactive;
using Ifs.Fnd.AccessProvider.PLSQL;
using Ifs.Fnd.Data;

namespace Myklink.IoT.Watcher
{
    internal enum AlertKind { Binary, Threshold }
    internal enum ThresholdDir { Up, Down }

    internal class Device
    {
        public string Name;
        public string IfsObjectId;
        public string Contract;
        public string Url;
        public AlertKind Kind;
        public int NormalBinary;
        public ThresholdDir Dir;
        public double ThresholdValue;
        public string ErrDescrTemplate;
        public string StateKey;
    }

    internal class DeviceState
    {
        public string LastValue;
        public int RetryQty;
        public int AlertOpen;
    }

    internal static class Program
    {
        static int RetryQtyThenAlert = 5;
        static string IfsUrl = string.Empty;
        static string ServiceUser = string.Empty;
        static string ServicePassword = string.Empty;
        static string ReportedBy = "WORKER1";
        static string ReportedById = "";
        static string OrgCode = "ADM";
        static bool DebugFlag = true;
        static string PMType = "";
        static string ConnectionTypeDb = "";
        static string SourceId = "";
        static string Source = "";
        static string EFlag = "DO";

        static List<Device> Devices = new List<Device>();
        static Dictionary<string, DeviceState> States = new Dictionary<string, DeviceState>(StringComparer.OrdinalIgnoreCase);
        static FndConnection IfsConn = null;
        static bool IfsSessionOpened = false;

        [STAThread]
        public static int Main(string[] args)
        {
            try
            {
                string confPath = (args != null && args.Length > 0) ? args[0] : "myklink_iot_watcher.conf";
                string statesPath = (args != null && args.Length > 1) ? args[1] : "myklink_iot_states.csv";

                if (!File.Exists(confPath))
                {
                    Console.Error.WriteLine("Config not found: " + confPath);
                    return 2;
                }

                LoadConfig(confPath);
                LoadStates(statesPath);

                HttpClient http = new HttpClient();
                http.Timeout = TimeSpan.FromSeconds(8);

                OpenIfsSession();

                for (int i = 0; i < Devices.Count; i++)
                {
                    Device dev = Devices[i];
                    try
                    {
                        Console.WriteLine("[INFO] " + dev.Name + " (" + dev.IfsObjectId + ") -> GET " + dev.Url);
                        string valueStr = FetchValue(http, dev.Url);

                        if (dev.Kind == AlertKind.Binary)
                        {
                            int current = ParseBinary(valueStr);
                            ProcessDeviceBinary(dev, current);
                        }
                        else
                        {
                            double current = ParseDouble(valueStr);
                            ProcessDeviceThreshold(dev, current);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("[ERROR] " + dev.IfsObjectId + ": " + ex.Message);
                    }
                }

                SaveStates(statesPath);
                CloseIfsSession();
                Console.WriteLine("[DONE]");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Fatal: " + ex.ToString());
                try { CloseIfsSession(); } catch { }
                return 1;
            }
        }

        static void ProcessDeviceBinary(Device dev, int current)
        {
            DeviceState st = GetOrInitState(dev, current.ToString(CultureInfo.InvariantCulture));

            bool isNormal = (current == dev.NormalBinary);
            ProcessEvaluation(dev, isNormal, current.ToString(CultureInfo.InvariantCulture), st);
        }

        static void ProcessDeviceThreshold(Device dev, double current)
        {
            DeviceState st = GetOrInitState(dev, current.ToString(CultureInfo.InvariantCulture));

            bool abnormal;
            if (dev.Dir == ThresholdDir.Up) abnormal = (current > dev.ThresholdValue); else abnormal = (current < dev.ThresholdValue);
            bool isNormal = !abnormal;
            ProcessEvaluation(dev, isNormal, current.ToString(CultureInfo.InvariantCulture), st);
        }

        static void ProcessEvaluation(Device dev, bool isNormal, string currentValueStr, DeviceState st)
        {
            if (isNormal)
            {
                if (st.RetryQty != 0 || st.LastValue != currentValueStr)
                    Console.WriteLine("[RESET] " + dev.IfsObjectId + " normal (" + currentValueStr + ")");
                st.RetryQty = 0;
                st.LastValue = currentValueStr;
                st.AlertOpen = 0;
                return;
            }

            if (st.LastValue != currentValueStr) st.RetryQty = 1; else if (st.RetryQty < RetryQtyThenAlert) st.RetryQty = st.RetryQty + 1;
            st.LastValue = currentValueStr;
            Console.WriteLine("[ABNORMAL] " + dev.IfsObjectId + " val=" + currentValueStr + " retry=" + st.RetryQty + "/" + RetryQtyThenAlert);

            if (st.RetryQty == RetryQtyThenAlert)
            {
                if (st.AlertOpen == 1)
                {
                    Console.WriteLine("[SKIP] " + dev.IfsObjectId + " already reported (waiting for normalization)");
                    return;
                }

                string errDescr = BuildErrDescr(dev, currentValueStr);
                string errDescrLo = (dev.Kind == AlertKind.Binary)
                    ? "IOT binary state abnormal"
                    : "IOT threshold exceeded (" + dev.Dir + " " + dev.ThresholdValue.ToString(CultureInfo.InvariantCulture) + ")";

                RaiseFaultReport(
                    dev.Contract,
                    ReportedBy,
                    string.IsNullOrEmpty(ReportedById) ? ReportedBy : ReportedById,
                    OrgCode,
                    dev.IfsObjectId,
                    dev.Contract,
                    errDescr,
                    errDescrLo,
                    SourceId,
                    Source,
                    PMType,
                    ConnectionTypeDb
                );

                st.AlertOpen = 1;
            }

        }

        static void OpenIfsSession()
        {
            IfsConn = new FndConnection();
            IfsConn.ConnectionString = IfsUrl;

            string user = (ServiceUser ?? "").Trim();
            string pass = (ServicePassword ?? "").Trim();

            if (user.Length > 0 && pass.Length > 0)
            {
                IfsConn.InteractiveMode = false;
                IfsConn.AutoLogon = false;
                try
                {
                    IfsConn.SetCredentials(user, pass);
                    IfsConn.OpenDedicatedSession();
                    IfsSessionOpened = true;
                    return;
                }
                catch (Exception ex)
                {
                    throw new Exception("IFS service login failed: " + ex.Message, ex);
                }
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            IfsConn.InteractiveMode = true;
            IfsConn.AutoLogon = true;
            FndLoginCredentials lc = FndLoginDialog.GetStoredCredentials();
            lc.ShowOptions = false;
            FndLoginDialog dlg = new FndLoginDialog(IfsConn);
            dlg.ShowDialog(lc, false);
            if (lc == null || string.IsNullOrEmpty(lc.UserName)) throw new InvalidOperationException("IFS login cancelled.");
            FndLoginDialog.StoreCredentials(lc);
            string identity = (lc.Identity != null && lc.Identity.Length > 0) ? lc.Identity : lc.UserName;
            IfsConn.SetCredentials(identity, lc.Password);
            IfsConn.OpenDedicatedSession();
            IfsSessionOpened = true;
        }

        static void CloseIfsSession()
        {
            if (IfsSessionOpened && IfsConn != null)
            {
                try { IfsConn.CloseDedicatedSession(true); } catch { }
            }
        }

        static void RaiseFaultReport(
            string contract,
            string reportedBy,
            string reportedById,
            string orgCode,
            string mchCode,
            string mchCodeContract,
            string errDescr,
            string errDescrLo,
            string sourceId,
            string source,
            string pmType,
            string connectionTypeDb)
        {
            string RS = ((char)30).ToString();
            string US = ((char)31).ToString();
            string regDate = NowInPoland().ToString("yyyy-MM-dd-HH.mm.ss", CultureInfo.InvariantCulture);

            StringBuilder sb = new StringBuilder();
            AppendKV(sb, "REG_DATE", regDate, RS, US);
            AppendKV(sb, "CONTRACT", contract, RS, US);
            AppendKV(sb, "REPORTED_BY", reportedBy, RS, US);
            AppendKV(sb, "REPORTED_BY_ID", reportedById, RS, US);
            AppendKV(sb, "ERR_DESCR", errDescr, RS, US);
            AppendKV(sb, "ORG_CODE", orgCode, RS, US);
            AppendKV(sb, "MCH_CODE", mchCode, RS, US);
            AppendKV(sb, "MCH_CODE_CONTRACT", mchCodeContract, RS, US);
            AppendKV(sb, "ERR_DESCR_LO", errDescrLo, RS, US);
            AppendKV(sb, "SOURCE_ID", sourceId, RS, US);
            AppendKV(sb, "SOURCE", source, RS, US);
            if (!string.IsNullOrEmpty(pmType)) AppendKV(sb, "PM_TYPE", pmType, RS, US);
            if (!string.IsNullOrEmpty(connectionTypeDb)) AppendKV(sb, "CONNECTION_TYPE_DB", connectionTypeDb, RS, US);

            string dPayload = sb.ToString();
            while (dPayload.IndexOf(RS + RS, StringComparison.Ordinal) >= 0)
                dPayload = dPayload.Replace(RS + RS, RS);

            string eFlag = string.IsNullOrEmpty(EFlag) ? "DO" : EFlag.Trim().ToUpperInvariant();

            if (DebugFlag)
            {
                Console.WriteLine("[DEBUG] IFSAPP.FAULT_REPORT_API.NEW__");
                Console.WriteLine("[DEBUG] d_ => " + dPayload);
                Console.WriteLine("[DEBUG] e_ => " + eFlag);
                return;
            }

            const string plsql = "DECLARE a_ VARCHAR2(32000) := NULL; b_ VARCHAR2(32000) := NULL; c_ VARCHAR2(32000) := NULL; d_ VARCHAR2(32000) := :P_D; e_ VARCHAR2(32000) := :P_E; BEGIN IFSAPP.FAULT_REPORT_API.NEW__(a_, b_, c_, d_, e_); commit; END;";

            FndPLSQLSelectCommand cmd = new FndPLSQLSelectCommand(IfsConn, plsql);
            cmd.BindVariables.Add(new FndBindVariable(FndBindVariableDirection.In, "P_D", new FndTextAttribute(dPayload)));
            cmd.BindVariables.Add(new FndBindVariable(FndBindVariableDirection.In, "P_E", new FndTextAttribute(eFlag)));
            try
            {
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Console.WriteLine("[IFS ERROR] AccessPlsql/Invoke failed");
                Console.WriteLine("Message: " + ex.Message);
                if (ex.InnerException != null)
                {
                    Console.WriteLine("Inner: " + ex.InnerException.Message);
                    if (ex.InnerException.InnerException != null)
                        Console.WriteLine("Inner2: " + ex.InnerException.InnerException.Message);
                }
                Console.WriteLine("[IFS ERROR] d_ payload was:\n" + dPayload.Replace(((char)31).ToString(), "=").Replace(((char)30).ToString(), "|"));
                throw;
            }

            Console.WriteLine("[IFS] FAULT_REPORT_API.NEW__ executed for " + mchCode + " at " + regDate + " (contract=" + contract + ")");
        }

        static void AppendKV(StringBuilder sb, string key, string val, string RS, string US)
        {
            sb.Append(key); sb.Append(US); sb.Append(val); sb.Append(RS);
        }

        static DateTime NowInPoland()
        {
            try
            {
                TimeZoneInfo tz = TimeZoneInfo.FindSystemTimeZoneById("Central European Standard Time");
                return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
            }
            catch { return DateTime.Now; }
        }

        static void LoadConfig(string path)
        {
            Devices.Clear();
            string[] lines = File.ReadAllLines(path);
            bool inDevices = false;
            int lineNo = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                lineNo++;
                string line = (lines[i] ?? "").Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("#") || line.StartsWith(";")) continue;

                if (string.Equals(line, "[devices]", StringComparison.OrdinalIgnoreCase)) { inDevices = true; continue; }

                if (!inDevices)
                {
                    int eq = line.IndexOf('=');
                    if (eq > 0)
                    {
                        string key = line.Substring(0, eq).Trim();
                        string val = line.Substring(eq + 1).Trim();
                        int semi = val.IndexOf(';');
                        if (semi >= 0) val = val.Substring(0, semi).Trim();

                        if (string.Equals(key, "retry_qty_then_alert", StringComparison.OrdinalIgnoreCase)) { int n; if (int.TryParse(val, out n) && n > 0) RetryQtyThenAlert = n; }
                        else if (string.Equals(key, "ifs_url", StringComparison.OrdinalIgnoreCase)) IfsUrl = val;
                        else if (string.Equals(key, "service_user", StringComparison.OrdinalIgnoreCase) || string.Equals(key, "ifs_user", StringComparison.OrdinalIgnoreCase)) ServiceUser = val;
                        else if (string.Equals(key, "service_password", StringComparison.OrdinalIgnoreCase) || string.Equals(key, "ifs_password", StringComparison.OrdinalIgnoreCase)) ServicePassword = val;
                        else if (string.Equals(key, "reported_by", StringComparison.OrdinalIgnoreCase)) ReportedBy = val;
                        else if (string.Equals(key, "reported_by_id", StringComparison.OrdinalIgnoreCase)) ReportedById = val;
                        else if (string.Equals(key, "pm_type", StringComparison.OrdinalIgnoreCase)) PMType = val;
                        else if (string.Equals(key, "connection_type_db", StringComparison.OrdinalIgnoreCase)) ConnectionTypeDb = val;
                        else if (string.Equals(key, "source_id", StringComparison.OrdinalIgnoreCase)) SourceId = val;
                        else if (string.Equals(key, "source", StringComparison.OrdinalIgnoreCase)) Source = val;
                        else if (string.Equals(key, "e_flag", StringComparison.OrdinalIgnoreCase)) EFlag = val;
                        else if (string.Equals(key, "org_code", StringComparison.OrdinalIgnoreCase)) OrgCode = val;
                        else if (string.Equals(key, "debug", StringComparison.OrdinalIgnoreCase)) DebugFlag = ParseBool(val);
                    }
                }
                else
                {
                    string[] parts = SplitCsv(line);
                    if (parts.Length < 6) throw new Exception("Invalid device row at line " + lineNo);

                    Device d = new Device();
                    d.Name = parts[0].Trim();
                    d.IfsObjectId = parts[1].Trim();
                    d.Contract = parts[2].Trim();
                    d.Url = parts[3].Trim();
                    string type = parts[4].Trim().ToUpperInvariant();
                    string last = parts[5].Trim();
                    string errTpl = (parts.Length >= 7) ? parts[6].Trim() : string.Empty;

                    if (type == "BINARY")
                    {
                        d.Kind = AlertKind.Binary;
                        int normal;
                        if (!int.TryParse(last, out normal) || (normal != 0 && normal != 1)) throw new Exception("normal_value must be 0/1 at line " + lineNo);
                        d.NormalBinary = normal;
                        d.ErrDescrTemplate = errTpl;
                    }
                    else if (type.StartsWith("THRESHOLD:"))
                    {
                        d.Kind = AlertKind.Threshold;
                        string[] sp = type.Split(':');
                        string dir = (sp.Length > 1) ? sp[1] : "";
                        if (dir == "UP") d.Dir = ThresholdDir.Up; else if (dir == "DOWN") d.Dir = ThresholdDir.Down; else throw new Exception("THRESHOLD dir must be UP or DOWN at line " + lineNo);
                        double thr;
                        if (!double.TryParse(last, NumberStyles.Float, CultureInfo.InvariantCulture, out thr)) throw new Exception("threshold_value must be numeric at line " + lineNo);
                        d.ThresholdValue = thr;
                        d.ErrDescrTemplate = errTpl;
                    }
                    else
                    {
                        throw new Exception("alert_type must be BINARY or THRESHOLD:UP|DOWN at line " + lineNo);
                    }
                    d.StateKey = (d.IfsObjectId + "::" + d.Name).Trim();
                    Devices.Add(d);
                }
            }

            if (string.IsNullOrEmpty(ReportedById)) ReportedById = ReportedBy;
            if (IfsUrl.Length == 0) throw new Exception("ifs_url must be set in config");
        }

        static void LoadStates(string path)
        {
            States.Clear();
            if (!File.Exists(path))
            {
                Console.WriteLine("[INFO] States not found, will create: " + path);
                return;
            }
            string[] lines = File.ReadAllLines(path);
            int lineNo = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                lineNo++;
                string line = (lines[i] ?? "").Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("#") || line.StartsWith(";")) continue;
                string[] parts = line.Split(',');
                if (parts.Length < 3) { Console.WriteLine("[WARN] Bad states row at line " + lineNo); continue; }

                string id = parts[0].Trim();
                string last = parts[1].Trim();

                int retry; if (!int.TryParse(parts[2].Trim(), out retry)) retry = 0;
                int open = 0;
                if (parts.Length >= 4) { int.TryParse(parts[3].Trim(), out open); }

                DeviceState st = new DeviceState();
                st.LastValue = last;
                st.RetryQty = retry;
                st.AlertOpen = open;

                States[id] = st;
            }
        }

        static void SaveStates(string path)
        {
            string tmp = path + ".tmp";
            using (StreamWriter sw = new StreamWriter(tmp, false, new UTF8Encoding(false)))
            {
                sw.WriteLine("# ifs_object_id,last_value,retry_qty,alert_open");
                List<string> keys = new List<string>(States.Keys);
                keys.Sort(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < keys.Count; i++)
                {
                    DeviceState st = States[keys[i]];
                    sw.WriteLine(keys[i] + "," + st.LastValue + "," + st.RetryQty + "," + st.AlertOpen);
                }
            }
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        static DeviceState GetOrInitState(Device dev, string initial)
        {
            string key = dev.StateKey;
            DeviceState st;
            if (!States.TryGetValue(key, out st))
            {
                // migracja ze starego formatu: jeśli był wpis pod samym ifs_object_id, przenieś go
                if (States.TryGetValue(dev.IfsObjectId, out st))
                {
                    States.Remove(dev.IfsObjectId);
                }
                else
                {
                    st = new DeviceState();
                    st.LastValue = initial;
                    st.RetryQty = 0;
                    st.AlertOpen = 0;
                }
                States[key] = st;
            }
            return st;
        }


        static string BuildErrDescr(Device dev, string currentVal)
        {
            string tpl = dev.ErrDescrTemplate;
            if (tpl != null && tpl.Length > 0)
            {
                try
                {
                    string s = tpl.Replace("{name}", dev.Name).Replace("{value}", currentVal);
                    return s;
                }
                catch { return tpl; }
            }
            return "URGENT! Auto ticket. Device=" + dev.Name + " Value=" + currentVal;
        }

        static string[] SplitCsv(string line)
        {
            List<string> result = new List<string>();
            StringBuilder sb = new StringBuilder();
            bool inQuotes = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '"')
                {
                    if (inQuotes && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else { inQuotes = !inQuotes; }
                }
                else if (c == ',' && !inQuotes) { result.Add(sb.ToString()); sb.Length = 0; }
                else { sb.Append(c); }
            }
            result.Add(sb.ToString());
            return result.ToArray();
        }

        static string FetchValue(HttpClient http, string url)
        {
            HttpResponseMessage resp = http.GetAsync(url).GetAwaiter().GetResult();
            resp.EnsureSuccessStatusCode();
            string text = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if (text == null) text = string.Empty; text = text.Trim();
            bool hasDigit = false; for (int i = 0; i < text.Length; i++) { if (char.IsDigit(text[i])) { hasDigit = true; break; } }
            if (!hasDigit) return text;
            int j = text.Length - 1; while (j >= 0 && !char.IsDigit(text[j]) && text[j] != '-' && text[j] != '.') j--; if (j < 0) return text;
            int end = j; while (j >= 0 && (char.IsDigit(text[j]) || text[j] == '-' || text[j] == '.')) j--;
            return text.Substring(j + 1, end - j).Trim();
        }

        static int ParseBinary(string value)
        {
            if (value == null) value = ""; value = value.Trim();
            if (value == "0") return 0; if (value == "1") return 1;
            int n; if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) { if (n == 0 || n == 1) return n; }
            throw new Exception("Expected 0/1, got '" + value + "'");
        }

        static double ParseDouble(string value)
        {
            double d; if (double.TryParse((value ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
            throw new Exception("Expected numeric value, got '" + value + "'");
        }

        static bool ParseBool(string s)
        {
            if (s == null) return false; string v = s.Trim().ToLowerInvariant();
            return v == "1" || v == "true" || v == "yes" || v == "y" || v == "on";
        }
    }
}
