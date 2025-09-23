//
// MykLink NBP CSV Dumper
// myklink.pl
// https://github.com/MykLinkPl
// Author: Przemysław Myk
//
// Description:
// Fetches the latest NBP exchange rate tables (A/B/C) and saves them into CSV files.
// Configuration is read from MykLinkNBP.conf (auto-created on first run).
//

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace MykLinkNbp
{
    internal static class Program
    {
        // -----------------------
        // Defaults (used if .conf missing keys)
        // -----------------------
        private const string DEFAULT_OUTPUT_DIR = ".";
        private const string DEFAULT_FILE_A_NAME = "nbp_A.csv";
        private const string DEFAULT_FILE_B_NAME = "nbp_B.csv";
        private const string DEFAULT_FILE_C_NAME = "nbp_C.csv";
        private const string DEFAULT_LOG_FILENAME = "myk_nbp_log.txt";
        private const int LOG_MAX_LINES = 1000;

        // NBP publishes some currencies per 100 units
        private static readonly Dictionary<string, int> DEFAULT_CONV_FACTOR =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                { "JPY", 100 },
                { "HUF", 100 },
                { "ISK", 100 }
            };

        private static readonly object _logLock = new object();

        private static int Main(string[] args)
        {
            // 1) Resolve config path: same dir as the EXE
            string exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".";
            string confPath = Path.Combine(exeDir, "MykLinkNBP.conf");

            // 2) Create default config if missing
            if (!File.Exists(confPath))
                WriteDefaultConfig(confPath);

            // 3) Load config
            var cfg = LoadConfig(confPath);

            // 4) Allow CLI arg[0] to override OUTPUT_DIR (optional quality-of-life)
            if (args != null && args.Length >= 1 && !string.IsNullOrWhiteSpace(args[0]))
                cfg.OutputDir = args[0].Trim();

            // 5) Ensure output dir exists
            if (!Directory.Exists(cfg.OutputDir))
                Directory.CreateDirectory(cfg.OutputDir);

            string logPath = Path.Combine(cfg.OutputDir, cfg.LogFileName);

            try
            {
                WriteLog(logPath, "START – fetching NBP tables A/B/C");

                DumpTable("A",
                          Path.Combine(cfg.OutputDir, cfg.FileA),
                          logPath,
                          cfg.SelectedA,
                          cfg.ConvFactor);

                DumpTable("B",
                          Path.Combine(cfg.OutputDir, cfg.FileB),
                          logPath,
                          cfg.SelectedB,
                          cfg.ConvFactor);

                DumpTable("C",
                          Path.Combine(cfg.OutputDir, cfg.FileC),
                          logPath,
                          cfg.SelectedC,
                          cfg.ConvFactor);

                WriteLog(logPath, $"DONE – files saved: {cfg.FileA}, {cfg.FileB}, {cfg.FileC}");
                Console.WriteLine("OK: CSV files saved to {0}", cfg.OutputDir);
                return 0;
            }
            catch (Exception ex)
            {
                // Log and print error
                try { WriteLog(logPath, "ERROR: " + ex.Message); } catch { /* ignore */ }
                Console.Error.WriteLine("ERROR: " + ex.Message);
                return 1;
            }
        }

        // -----------------------
        // The main dumping routine
        // -----------------------
        private static void DumpTable(
            string tableLetter,
            string csvPath,
            string logPath,
            HashSet<string> selected,
            Dictionary<string, int> convFactor)
        {
            string json = HttpGetLatestTable(tableLetter);
            ParsedTable parsed = ParseNbpTableJson(json);

            var ci = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.AppendLine("currency_code,valid_from,currency_rate,conv_factor");

            int written = 0;

            foreach (var r in parsed.Rates)
            {
                // Filter per table if selection present
                if (selected != null && selected.Count > 0 && !selected.Contains(r.Code))
                    continue;

                // A/B -> mid; C -> average(bid, ask)
                decimal rate = r.Mid.HasValue
                               ? r.Mid.Value
                               : ((r.Bid ?? 0m) + (r.Ask ?? 0m)) / 2m;

                int factor;
                if (!convFactor.TryGetValue(r.Code, out factor))
                    factor = 1;

                sb.AppendLine(string.Join(",",
                    r.Code,
                    parsed.EffectiveDate ?? "",
                    rate.ToString(ci),
                    factor.ToString(ci)
                ));
                written++;
            }

            File.WriteAllText(csvPath, sb.ToString(), Encoding.UTF8);
            WriteLog(logPath, $"CSV saved: {Path.GetFileName(csvPath)} (records={written}, date={parsed.EffectiveDate})");
        }

        // -----------------------
        // HTTP
        // -----------------------
        private static string HttpGetLatestTable(string tableLetter)
        {
            string url = "https://api.nbp.pl/api/exchangerates/tables/" + tableLetter + "?format=json";
            using (var http = new HttpClient())
            {
                http.Timeout = TimeSpan.FromSeconds(30);
                var resp = http.GetAsync(url).GetAwaiter().GetResult();
                string body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                if (!resp.IsSuccessStatusCode)
                    throw new Exception("NBP HTTP " + (int)resp.StatusCode + ": " + body);
                return body;
            }
        }

        // -----------------------
        // JSON parsing (regex-based)
        // -----------------------
        private static ParsedTable ParseNbpTableJson(string json)
        {
            if (string.IsNullOrEmpty(json))
                throw new ArgumentException("empty json");

            string s = json;

            // effectiveDate
            string eff = MatchGroup(s, "\"effectiveDate\"\\s*:\\s*\"([^\"]+)\"", 1);

            // rates array
            string ratesArray = MatchGroup(s, "\"rates\"\\s*:\\s*\\[(.*)\\]\\s*\\}\\s*\\]?", 1, RegexOptions.Singleline);
            if (string.IsNullOrEmpty(ratesArray))
                throw new Exception("NBP: rates array not found");

            var rates = new List<ParsedRate>();
            var objRegex = new Regex("\\{[^\\{\\}]*\"code\"\\s*:\\s*\"([A-Z]{3})\"[^\\{\\}]*\\}", RegexOptions.Singleline);
            var matches = objRegex.Matches(ratesArray);
            foreach (Match m in matches)
            {
                string obj = m.Value;
                string code = MatchGroup(obj, "\"code\"\\s*:\\s*\"([A-Z]{3})\"", 1);
                if (string.IsNullOrEmpty(code)) continue;

                decimal? mid = TryParseDecimal(MatchGroup(obj, "\"mid\"\\s*:\\s*([0-9]+(?:\\.[0-9]+)?)", 1));
                decimal? bid = TryParseDecimal(MatchGroup(obj, "\"bid\"\\s*:\\s*([0-9]+(?:\\.[0-9]+)?)", 1));
                decimal? ask = TryParseDecimal(MatchGroup(obj, "\"ask\"\\s*:\\s*([0-9]+(?:\\.[0-9]+)?)", 1));

                rates.Add(new ParsedRate { Code = code, Mid = mid, Bid = bid, Ask = ask });
            }

            return new ParsedTable { EffectiveDate = eff, Rates = rates };
        }

        private static string MatchGroup(string input, string pattern, int groupIndex, RegexOptions opts = RegexOptions.None)
        {
            var m = Regex.Match(input, pattern, opts);
            return m.Success ? m.Groups[groupIndex].Value : null;
        }

        private static decimal? TryParseDecimal(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            decimal d;
            if (decimal.TryParse(s, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out d))
                return d;
            return null;
        }

        // -----------------------
        // Logging with hard rotation
        // -----------------------
        private static void WriteLog(string logPath, string message)
        {
            string line = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + message;
            lock (_logLock)
            {
                File.AppendAllText(logPath, line + Environment.NewLine, Encoding.UTF8);

                try
                {
                    string[] lines = File.ReadAllLines(logPath, Encoding.UTF8);
                    if (lines.Length > LOG_MAX_LINES)
                    {
                        int start = lines.Length - LOG_MAX_LINES;
                        var trimmed = new List<string>(LOG_MAX_LINES);
                        for (int i = start; i < lines.Length; i++)
                            trimmed.Add(lines[i]);

                        File.WriteAllText(logPath,
                            string.Join(Environment.NewLine, trimmed) + Environment.NewLine,
                            Encoding.UTF8);
                    }
                }
                catch { /* ignore log errors */ }
            }
        }

        // -----------------------
        // Config I/O
        // -----------------------
        private static void WriteDefaultConfig(string confPath)
        {
            // Simple INI-like key=value; comma-separated lists for selections; comments start with # or ;
            var sb = new StringBuilder();
            sb.AppendLine("# MykLink NBP CSV Dumper configuration");
            sb.AppendLine("# myklink.pl  |  https://github.com/MykLinkPl");
            sb.AppendLine("# Lines starting with # or ; are comments.");
            sb.AppendLine("# Empty SELECTED_* means: export ALL currencies from that table.");
            sb.AppendLine();
            sb.AppendLine("OUTPUT_DIR=.");
            sb.AppendLine("FILE_A_NAME=nbp_A.csv");
            sb.AppendLine("FILE_B_NAME=nbp_B.csv");
            sb.AppendLine("FILE_C_NAME=nbp_C.csv");
            sb.AppendLine("LOG_FILE_NAME=myk_nbp_log.txt");
            sb.AppendLine();
            sb.AppendLine("# SELECTED_A=EUR,USD");
            sb.AppendLine("# SELECTED_B=");
            sb.AppendLine("# SELECTED_C=");
            sb.AppendLine();
            sb.AppendLine("# Optional: override/extend conversion factors (unit multipliers).");
            sb.AppendLine("# Format: CODE:FACTOR pairs separated by commas. Example adds 100 for JPY/HUF/ISK:");
            sb.AppendLine("# CONV_FACTOR=JPY:100,HUF:100,ISK:100");
            File.WriteAllText(confPath, sb.ToString(), Encoding.UTF8);
        }

        private static AppConfig LoadConfig(string confPath)
        {
            var cfg = new AppConfig
            {
                OutputDir = DEFAULT_OUTPUT_DIR,
                FileA = DEFAULT_FILE_A_NAME,
                FileB = DEFAULT_FILE_B_NAME,
                FileC = DEFAULT_FILE_C_NAME,
                LogFileName = DEFAULT_LOG_FILENAME,
                SelectedA = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                SelectedB = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                SelectedC = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                ConvFactor = new Dictionary<string, int>(DEFAULT_CONV_FACTOR, StringComparer.OrdinalIgnoreCase)
            };

            string[] lines = File.ReadAllLines(confPath, Encoding.UTF8);
            foreach (var raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("#") || line.StartsWith(";")) continue;

                int eq = line.IndexOf('=');
                if (eq < 0) continue;

                string key = line.Substring(0, eq).Trim();
                string val = line.Substring(eq + 1).Trim();

                if (key.Equals("OUTPUT_DIR", StringComparison.OrdinalIgnoreCase))
                    cfg.OutputDir = val.Length == 0 ? DEFAULT_OUTPUT_DIR : val;

                else if (key.Equals("FILE_A_NAME", StringComparison.OrdinalIgnoreCase))
                    cfg.FileA = val.Length == 0 ? DEFAULT_FILE_A_NAME : val;

                else if (key.Equals("FILE_B_NAME", StringComparison.OrdinalIgnoreCase))
                    cfg.FileB = val.Length == 0 ? DEFAULT_FILE_B_NAME : val;

                else if (key.Equals("FILE_C_NAME", StringComparison.OrdinalIgnoreCase))
                    cfg.FileC = val.Length == 0 ? DEFAULT_FILE_C_NAME : val;

                else if (key.Equals("LOG_FILE_NAME", StringComparison.OrdinalIgnoreCase))
                    cfg.LogFileName = val.Length == 0 ? DEFAULT_LOG_FILENAME : val;

                else if (key.Equals("SELECTED_A", StringComparison.OrdinalIgnoreCase))
                    cfg.SelectedA = ParseCsvSet(val);

                else if (key.Equals("SELECTED_B", StringComparison.OrdinalIgnoreCase))
                    cfg.SelectedB = ParseCsvSet(val);

                else if (key.Equals("SELECTED_C", StringComparison.OrdinalIgnoreCase))
                    cfg.SelectedC = ParseCsvSet(val);

                else if (key.Equals("CONV_FACTOR", StringComparison.OrdinalIgnoreCase))
                    MergeConvFactors(cfg.ConvFactor, val);
            }

            return cfg;
        }

        private static HashSet<string> ParseCsvSet(string csv)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(csv)) return set;

            string[] parts = csv.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                string cur = parts[i].Trim();
                if (cur.Length > 0) set.Add(cur.ToUpperInvariant());
            }
            return set;
        }

        private static void MergeConvFactors(Dictionary<string, int> target, string spec)
        {
            if (string.IsNullOrEmpty(spec)) return;

            string[] pairs = spec.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var p in pairs)
            {
                int sep = p.IndexOf(':');
                if (sep <= 0) continue;

                string code = p.Substring(0, sep).Trim().ToUpperInvariant();
                string val = p.Substring(sep + 1).Trim();
                int factor;
                if (code.Length == 3 && int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out factor) && factor > 0)
                {
                    target[code] = factor;
                }
            }
        }
    }

    // -----------------------
    // Config structure
    // -----------------------
    internal sealed class AppConfig
    {
        public string OutputDir { get; set; }
        public string FileA { get; set; }
        public string FileB { get; set; }
        public string FileC { get; set; }
        public string LogFileName { get; set; }

        public HashSet<string> SelectedA { get; set; }
        public HashSet<string> SelectedB { get; set; }
        public HashSet<string> SelectedC { get; set; }

        public Dictionary<string, int> ConvFactor { get; set; }
    }

    // -----------------------
    // Minimal JSON model + regex parsing support
    // -----------------------
    internal sealed class ParsedTable
    {
        public string EffectiveDate { get; set; }
        public List<ParsedRate> Rates { get; set; }
        public ParsedTable() { Rates = new List<ParsedRate>(); }
    }

    internal sealed class ParsedRate
    {
        public string Code { get; set; }
        public decimal? Mid { get; set; } // A/B
        public decimal? Bid { get; set; } // C
        public decimal? Ask { get; set; } // C
    }

  
}
