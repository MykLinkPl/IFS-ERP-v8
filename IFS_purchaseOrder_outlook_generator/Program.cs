using Ifs.Fnd.AccessProvider;
using Ifs.Fnd.AccessProvider.Interactive;
using Ifs.Fnd.AccessProvider.PLSQL;
using Ifs.Fnd.Data;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using System.Text;
using System.Threading;
using Newtonsoft.Json;

namespace MykLink_IFSPO_outlook_printer
{
    internal class ConfigModel
    {
        public string defaultRecipient { get; set; }
        public Dictionary<string, string> environments { get; set; }
        public List<string> allowedStates { get; set; }
        public MailTexts mail { get; set; }
        public string supplierCommName { get; set; }
    }

    internal class MailTexts
    {
        public MailLang pl { get; set; }
        public MailLang en { get; set; }
    }

    internal class MailLang
    {
        public string subject { get; set; }
        public string body { get; set; }
    }

    internal static class Program
    {
        private static ConfigModel config;

        [STAThread]
        static int Main(string[] args)
        {
            LoadConfig();
            if (args.Length != 2)
            {
                Console.WriteLine("Usage: MykLink_IFSPO_outlook_printer.exe <env> <order_no>");
                return 1;
            }

            string env = args[0].Trim();
            string orderNo = args[1].Trim();

            if (!config.environments.ContainsKey(env))
            {
                Console.WriteLine($"Unknown environment '{env}'. Allowed: {string.Join(", ", config.environments.Keys)}");
                return 2;
            }

            string connUrl = config.environments[env];

            try
            {
                var conn = new FndConnection { ConnectionString = connUrl, InteractiveMode = true, AutoLogon = false };
                var dlg = new FndLoginDialog(conn);
                var creds = new FndLoginCredentials { ShowOptions = false };
                var dr = dlg.ShowDialog(creds, false);
                if (dr != DialogResult.OK || string.IsNullOrEmpty(creds.UserName))
                {
                    Console.WriteLine("Login cancelled.");
                    return 4;
                }

                conn.SetCredentials(!string.IsNullOrEmpty(creds.Identity) ? creds.Identity : creds.UserName, creds.Password);
                conn.OpenDedicatedSession();

                RunBusiness(conn, orderNo);

                conn.CloseDedicatedSession(true);
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Fatal error: {ex.Message}");
                return 99;
            }
        }
        static List<string> NormalizeRecipients(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return new List<string>();
            var parts = raw
                .Replace("\r", " ").Replace("\n", " ")
                .Replace("\t", " ")
                .Split(new[] { ';', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Contains("@"))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return parts;
        }
        static string GetWorkingFolder()
        {
            string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            return Path.Combine(docs, "Purchase_Orders");
        }

        static void CleanWorkingFolder()
        {
            var folder = GetWorkingFolder();
            if (!Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);
                return;
            }

            // kasuj wszystkie pliki
            foreach (var file in Directory.GetFiles(folder, "*", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                    File.Delete(file);
                }
                catch { /* ignoruj pojedyncze błędy */ }
            }

            // kasuj wszystkie podkatalogi
            foreach (var dir in Directory.GetDirectories(folder, "*", SearchOption.TopDirectoryOnly))
            {
                try { Directory.Delete(dir, recursive: true); }
                catch { /* ignoruj pojedyncze błędy */ }
            }
        }

        static void LoadConfig()
        {
            CleanWorkingFolder();
            string cfgPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "myklink_conf.json");
            if (!File.Exists(cfgPath))
            {
                Console.WriteLine("Missing configuration file 'myklink_conf.json'.");
                Environment.Exit(10);
            }

            try
            {
                string json = File.ReadAllText(cfgPath);
                config = JsonConvert.DeserializeObject<ConfigModel>(json);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Invalid configuration file: " + ex.Message);
                Environment.Exit(11);
            }
        }

        static void RunBusiness(FndConnection conn, string orderNo)
        {
            var q = @"
                SELECT
                    ifsapp.Purchase_Order_Api.Get_Objstate__(:P_ORDER_NO) AS OBJSTATE,
                    ifsapp.Purchase_Order_Api.Get_Language_Code(:P_ORDER_NO) AS LANG,
                    CASE
                        WHEN ifsapp.Purchase_Order_Api.Get_Language_Code(:P_ORDER_NO)='pl'
                        THEN 'Zamowienie_zakupu_nr_'||:P_ORDER_NO||'.pdf'
                        ELSE 'Purchase_Order_'||:P_ORDER_NO||'.pdf'
                    END AS FILE_NAME
                FROM dual";

            var cmd = new FndPLSQLSelectCommand(conn, q);
            cmd.BindVariables.Add(new FndBindVariable(FndBindVariableDirection.In, "P_ORDER_NO", new FndTextAttribute(orderNo)));
            var dt = cmd.ExecuteReader();
            if (dt.Count == 0)
            {
                Console.WriteLine("Purchase order not found.");
                return;
            }

            FndDataRow row = null;
            foreach (FndDataRow r in dt) { row = r; break; }
            if (row == null)
            {
                Console.WriteLine("Purchase order not found.");
                return;
            }

            string objstate = Convert.ToString(row["OBJSTATE"]);
            if (!config.allowedStates.Contains(objstate))
            {
                Console.WriteLine($"Order state '{objstate}' not allowed.");
                return;
            }

            string lang = Convert.ToString(row["LANG"])?.ToLower() ?? "en";
            string fileName = Convert.ToString(row["FILE_NAME"]);
            string mailTo = GetSupplierEmailsForOrder(conn, orderNo, config.supplierCommName);

            byte[] pdf = GeneratePoPdf(conn, orderNo, timeoutSeconds: 15, pollIntervalMs: 500);
            if (pdf == null)
            {
                Console.WriteLine("No PDF found in archive (timeout).");
                return;
            }

            ComposeMail(fileName, lang, mailTo, pdf, orderNo);
        }

        static string GetSupplierEmailsForOrder(FndConnection conn, string orderNo, string commName)
        {
            if (string.IsNullOrWhiteSpace(orderNo)) return null;

            const string sqlVendor = @"SELECT ifsapp.purchase_order_api.get_vendor_no(:P_ORDER_NO) AS VENDOR_NO FROM dual";
            var cmdVendor = new FndPLSQLSelectCommand(conn, sqlVendor);
            cmdVendor.BindVariables.Add(new FndBindVariable(FndBindVariableDirection.In, "P_ORDER_NO", new FndTextAttribute(orderNo)));
            var tblVendor = cmdVendor.ExecuteReader();
            if (tblVendor.Count == 0) return null;

            FndDataRow vrow = null;
            foreach (FndDataRow r in tblVendor) { vrow = r; break; }
            var vendorNo = vrow != null ? Convert.ToString(vrow["VENDOR_NO"]) : null;
            if (string.IsNullOrWhiteSpace(vendorNo)) return null;

            const string sqlEmails = @"
                SELECT value
                  FROM ifsapp.COMM_METHOD
                 WHERE party_type_db = 'SUPPLIER'
                   AND NAME = :P_COMM_NAME
                   AND identity = :P_VENDOR_NO
                 ORDER BY value";

            var cmdEmail = new FndPLSQLSelectCommand(conn, sqlEmails);
            cmdEmail.BindVariables.Add(new FndBindVariable(FndBindVariableDirection.In, "P_COMM_NAME", new FndTextAttribute(commName)));
            cmdEmail.BindVariables.Add(new FndBindVariable(FndBindVariableDirection.In, "P_VENDOR_NO", new FndTextAttribute(vendorNo)));
            var tblEmails = cmdEmail.ExecuteReader();
            if (tblEmails.Count == 0) return null;

            var list = new List<string>();
            foreach (FndDataRow rr in tblEmails)
            {
                var val = Convert.ToString(rr["VALUE"]);
                if (!string.IsNullOrWhiteSpace(val)) list.Add(val.Trim());
            }

            return list.Count > 0 ? string.Join(";", list) : null;
        }

        static byte[] GeneratePoPdf(FndConnection conn, string orderNo, int timeoutSeconds = 15, int pollIntervalMs = 500)
        {
            string runTagName = "MYKLINK_RUN_ID";
            string runTagValue = Guid.NewGuid().ToString("N");

            CreateArchiveAndContents(conn, orderNo, runTagName, runTagValue);
            PrintTaggedJob(conn, runTagName, runTagValue);

            var start = DateTime.UtcNow;
            while ((DateTime.UtcNow - start).TotalSeconds < timeoutSeconds)
            {
                var pdf = FetchPdfByRunTag(conn, runTagName, runTagValue);
                if (pdf != null && pdf.Length > 0) return pdf;
                Thread.Sleep(pollIntervalMs);
            }
            return null;
        }

        static void CreateArchiveAndContents(FndConnection conn, string orderNo, string runTagName, string runTagValue)
        {
            string plsql = @"
            BEGIN
              DECLARE
                report_attr_     VARCHAR2(2000);
                parameter_attr_  VARCHAR2(2000);
                print_job_id_    NUMBER;
                result_key_      NUMBER;
                attr_            VARCHAR2(2000);
                order_no_        VARCHAR2(255) := :order_num_;
                printer_id_      VARCHAR2(100) := 'PDF_PRINTER';
                run_tag_name_    VARCHAR2(50)  := :run_tag_name_;
                run_tag_value_   VARCHAR2(100) := :run_tag_value_;
              BEGIN
                Client_SYS.Clear_Attr(parameter_attr_);
                Client_SYS.Add_To_Attr('ORDER_NO_LIST', order_no_, parameter_attr_);
                Client_SYS.Add_To_Attr('PUR_ORDER_PRINT_OPTION', Pur_Order_Print_Option_API.Decode('ORDER'), parameter_attr_);
                Client_SYS.Add_To_Attr(run_tag_name_, run_tag_value_, parameter_attr_);

                Client_SYS.Clear_Attr(report_attr_);
                Client_SYS.Add_To_Attr('REPORT_ID', 'PURCHASE_ORDER_PRINT_REP', report_attr_);
                Client_SYS.Add_To_Attr('LU_NAME',   'PurchaseOrder',           report_attr_);
                Client_SYS.Set_Item_Value('LANG_CODE',      Purchase_Order_Api.Get_Language_Code(order_no_), report_attr_);
                Client_SYS.Set_Item_Value('ORDER_LANGUAGE', Purchase_Order_Api.Get_Language_Code(order_no_), report_attr_);

                Client_SYS.Clear_Attr(attr_);
                Client_SYS.Add_To_Attr('PRINTER_ID', printer_id_, attr_);
                Print_Job_API.New(print_job_id_, attr_);

                Archive_API.New_Instance(result_key_, report_attr_, parameter_attr_);

                Client_SYS.Clear_Attr(attr_);
                Client_SYS.Add_To_Attr('PRINT_JOB_ID', print_job_id_, attr_);
                Client_SYS.Add_To_Attr('RESULT_KEY',   result_key_,   attr_);
                Print_Job_Contents_API.New_Instance(attr_);
                commit;
              END;
            END;";
            var cmd = new FndPLSQLSelectCommand(conn, plsql);
            cmd.BindVariables.Add(new FndBindVariable(FndBindVariableDirection.In, "order_num_", new FndTextAttribute(orderNo)));
            cmd.BindVariables.Add(new FndBindVariable(FndBindVariableDirection.In, "run_tag_name_", new FndTextAttribute(runTagName)));
            cmd.BindVariables.Add(new FndBindVariable(FndBindVariableDirection.In, "run_tag_value_", new FndTextAttribute(runTagValue)));
            cmd.ExecuteNonQuery();
        }

        static void PrintTaggedJob(FndConnection conn, string runTagName, string runTagValue)
        {
                string plsql = @"
        BEGIN
          DECLARE
            pj_id_ NUMBER;
          BEGIN
            SELECT pc.print_job_id
              INTO pj_id_
              FROM ifsapp.print_job_contents pc
             WHERE pc.result_key IN (
                    SELECT p.result_key
                      FROM ifsapp.archive_parameter p
                     WHERE p.parameter_name  = :run_tag_name_
                       AND p.parameter_value = :run_tag_value_)
               AND ROWNUM = 1;

            IF pj_id_ IS NOT NULL THEN
              Print_Job_API.Print(pj_id_);
                commit;
            END IF;
          EXCEPTION
            WHEN NO_DATA_FOUND THEN
              NULL;
          END;
        END;";
                var cmd = new FndPLSQLSelectCommand(conn, plsql);
                cmd.BindVariables.Add(new FndBindVariable(FndBindVariableDirection.In, "run_tag_name_", new FndTextAttribute(runTagName)));
                cmd.BindVariables.Add(new FndBindVariable(FndBindVariableDirection.In, "run_tag_value_", new FndTextAttribute(runTagValue)));
                cmd.ExecuteNonQuery();
            }


        static byte[] FetchPdfByRunTag(FndConnection conn, string runTagName, string runTagValue)
        {
            const string pdfSql = @"
                SELECT A.PDF
                  FROM ifsapp.PDF_ARCHIVE A
                 WHERE EXISTS (
                       SELECT 1
                         FROM ifsapp.archive_parameter P
                        WHERE P.result_key      = A.result_key
                          AND P.parameter_name  = :P_TAG_NAME
                          AND P.parameter_value = :P_TAG_VALUE)";
            var getCmd = new FndPLSQLSelectCommand(conn, pdfSql);
            getCmd.BindVariables.Add(new FndBindVariable(FndBindVariableDirection.In, "P_TAG_NAME", new FndTextAttribute(runTagName)));
            getCmd.BindVariables.Add(new FndBindVariable(FndBindVariableDirection.In, "P_TAG_VALUE", new FndTextAttribute(runTagValue)));
            var tbl = getCmd.ExecuteReader();
            if (tbl.Count == 0) return null;

            FndDataRow first = null;
            foreach (FndDataRow r in tbl) { first = r; break; }
            if (first == null) return null;

            var attr = first["PDF"];
            var bytes = TryGetBytesFromAttribute(attr);
            if (bytes != null && bytes.Length > 0) return bytes;

            var text = Convert.ToString(attr);
            if (!string.IsNullOrWhiteSpace(text))
            {
                try { return Convert.FromBase64String(text.Trim()); } catch { }
            }
            return null;
        }

        static byte[] TryGetBytesFromAttribute(object attr)
        {
            if (attr == null) return null;
            if (attr is byte[] b) return b;

            var t = attr.GetType();

            string[] byteProps = { "Value", "BinaryValue", "Bytes", "Data", "RawValue" };
            foreach (var pName in byteProps)
            {
                var p = t.GetProperty(pName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (p != null && p.PropertyType == typeof(byte[]))
                {
                    var val = p.GetValue(attr, null) as byte[];
                    if (val != null) return val;
                }
            }

            string[] byteMethods = { "GetBytes", "ToArray", "GetBinary", "AsByteArray" };
            foreach (var mName in byteMethods)
            {
                var m = t.GetMethod(mName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (m != null && m.ReturnType == typeof(byte[]))
                {
                    var val = m.Invoke(attr, null) as byte[];
                    if (val != null) return val;
                }
            }

            string[] textProps = { "Value", "Text", "StringValue" };
            foreach (var pName in textProps)
            {
                var p = t.GetProperty(pName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (p != null && p.PropertyType == typeof(string))
                {
                    var s = p.GetValue(attr, null) as string;
                    if (!string.IsNullOrWhiteSpace(s))
                    {
                        try { return Convert.FromBase64String(s.Trim()); } catch { }
                    }
                }
            }

            return null;
        }

        static void ComposeMail(string fileName, string lang, string mailTo, byte[] pdfData, string orderNo)
        {
            string folder = GetWorkingFolder();
            Directory.CreateDirectory(folder);

            string pdfPath = Path.Combine(folder, fileName);
            File.WriteAllBytes(pdfPath, pdfData);

            string recipientRaw = string.IsNullOrWhiteSpace(mailTo) ? config.defaultRecipient : mailTo;
            var recipients = NormalizeRecipients(recipientRaw);
            if (recipients.Count == 0) recipients = NormalizeRecipients(config.defaultRecipient);

            var mailText = lang == "pl" ? config.mail.pl : config.mail.en;
            string subject = mailText.subject.Replace("{orderNo}", orderNo);
            string body = mailText.body.Replace("{orderNo}", orderNo);

            if (TryOutlookCom(recipients, subject, body, pdfPath)) return;

            string eml = SaveEml(recipients, subject, body, pdfPath, fileName);
            TryShellOpen(eml);
        }


        static bool TryOutlookCom(List<string> recipients, string subject, string body, string attachment)
        {
            try
            {
                var outlookType = Type.GetTypeFromProgID("Outlook.Application");
                if (outlookType == null) return false;
                dynamic app = Activator.CreateInstance(outlookType);
                dynamic mail = app.CreateItem(0);

                foreach (var r in recipients)
                    mail.Recipients.Add(r);
                try { mail.Recipients.ResolveAll(); } catch { }

                mail.Subject = subject;
                mail.HTMLBody = body;
                mail.Attachments.Add(attachment);
                mail.Display(false);
                return true;
            }
            catch { return false; }
        }


        static string SaveEml(List<string> recipients, string subject, string body, string pdfPath, string fileName)
        {
            string boundary = "----=_Part_" + Guid.NewGuid().ToString("N");
            var eml = new StringBuilder();

            var toHeader = string.Join(", ", recipients);
            eml.AppendLine($"To: {toHeader}");
            eml.AppendLine($"Subject: =?UTF-8?B?{Convert.ToBase64String(Encoding.UTF8.GetBytes(subject))}?=");
            eml.AppendLine("X-Unsent: 1");
            eml.AppendLine("MIME-Version: 1.0");
            eml.AppendLine($"Content-Type: multipart/mixed; boundary=\"{boundary}\"");
            eml.AppendLine();

            eml.AppendLine($"--{boundary}");
            eml.AppendLine("Content-Type: text/html; charset=utf-8");
            eml.AppendLine("Content-Transfer-Encoding: base64");
            eml.AppendLine();
            var bodyB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(body));
            for (int i = 0; i < bodyB64.Length; i += 76)
                eml.AppendLine(bodyB64.Substring(i, Math.Min(76, bodyB64.Length - i)));
            eml.AppendLine();

            eml.AppendLine($"--{boundary}");
            eml.AppendLine($"Content-Type: application/pdf; name=\"{fileName}\"");
            eml.AppendLine("Content-Transfer-Encoding: base64");
            eml.AppendLine($"Content-Disposition: attachment; filename=\"{fileName}\"");
            eml.AppendLine();
            string base64 = Convert.ToBase64String(File.ReadAllBytes(pdfPath));
            for (int i = 0; i < base64.Length; i += 76)
                eml.AppendLine(base64.Substring(i, Math.Min(76, base64.Length - i)));
            eml.AppendLine();
            eml.AppendLine($"--{boundary}--");

            string path = Path.Combine(Path.GetDirectoryName(pdfPath), Path.GetFileNameWithoutExtension(fileName) + ".eml");
            File.WriteAllText(path, eml.ToString(), Encoding.UTF8);
            return path;
        }


        static bool TryShellOpen(string path)
        {
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                return true;
            }
            catch { return false; }
        }
    }
}
