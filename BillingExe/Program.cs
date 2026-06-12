using System;
using System.Data.SqlClient;
using System.Configuration;
using System.Globalization;
using System.Net.Http;

class Program
{
    static void Main()
    {
        string connStr = ConfigurationManager.ConnectionStrings["SixTyEntities_ADO"].ConnectionString;
        InsertUsageReports(connStr);
        Console.WriteLine("Done.");
        //Console.ReadKey();
        Environment.Exit(0);
    }    
    private static string GetLogsDirectory()
    {
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;

        // If running from /bin, move to app root
        if (baseDir.EndsWith(@"\bin\", StringComparison.OrdinalIgnoreCase))
            baseDir = Directory.GetParent(baseDir).FullName;

        string logsDir = Path.Combine(baseDir, "Logs");
        Directory.CreateDirectory(logsDir);

        return logsDir;
    }
    private static string ResolvePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        // Absolute path
        if (Path.IsPathRooted(path))
            return path;

        // Handle "~"
        if (path.StartsWith("~"))
        {
            string relativePath = path.TrimStart('~', '/', '\\');

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            if (baseDir.EndsWith(@"\bin\", StringComparison.OrdinalIgnoreCase))
                baseDir = Directory.GetParent(baseDir).FullName;

            return Path.Combine(baseDir, relativePath);
        }

        return path;
    }
    public static void InsertUsageReports(string connStr)
    {
        string logsDir = GetLogsDirectory();
        string logFile = Path.Combine(logsDir, $"UsageReportLog_{DateTime.Now:yyyyMMdd}.txt");

        void Log(string message)
        {
            string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}";
            Console.WriteLine(line);
            try { File.AppendAllText(logFile, line + Environment.NewLine); } catch { }
        }

        try
        {
            Log("Process Start");

            using (SqlConnection conn = new SqlConnection(connStr))
            {
                conn.Open();

               
                // ===========================
                // PATHS FROM APP SETTINGS
                // ===========================
                string dataBilledPath = ResolvePath(ConfigurationManager.AppSettings["DataUsageBilledPath"]);
                string dataUnbilledPath = ResolvePath(ConfigurationManager.AppSettings["DataUsageUnbilledPath"]);
                string smsBilledPath = ResolvePath(ConfigurationManager.AppSettings["SMSUsageBilledPath"]);
                string smsUnbilledPath = ResolvePath(ConfigurationManager.AppSettings["SMSUsageUnbilledPath"]);

                if (string.IsNullOrWhiteSpace(dataBilledPath) ||
                    string.IsNullOrWhiteSpace(dataUnbilledPath) ||
                    string.IsNullOrWhiteSpace(smsBilledPath) ||
                    string.IsNullOrWhiteSpace(smsUnbilledPath))
                {
                    throw new Exception("One or more Data/SMS paths are missing in AppSettings.");
                }

                Directory.CreateDirectory(dataBilledPath);
                Directory.CreateDirectory(dataUnbilledPath);
                Directory.CreateDirectory(smsBilledPath);
                Directory.CreateDirectory(smsUnbilledPath);

                DateTime today = DateTime.Today;
                Random rnd = new Random();

                // ===========================
                // GET ACTIVE RESELLERS
                // ===========================

                // Get active resellers
                var resellers = new List<(int Id, string Name, int StartDay, int EndDay)>();
                string resellerQuery = @"SELECT reseller_id, name, billing_start_day, billing_end_day FROM tbl_resellers WHERE is_active = 1";
                using (SqlCommand cmd = new SqlCommand(resellerQuery, conn))
                using (SqlDataReader rdr = cmd.ExecuteReader())
                {
                    while (rdr.Read())
                    {
                        int id = rdr.GetInt32(0);
                        string name = rdr.IsDBNull(1) ? "" : rdr.GetString(1);
                        int startDay = rdr.IsDBNull(2) ? 1 : Convert.ToInt32(rdr.GetValue(2));
                        int endDay = rdr.IsDBNull(3) ? 1 : Convert.ToInt32(rdr.GetValue(3));
                        resellers.Add((id, name, startDay, endDay));
                    }
                }

                // Helper: Delete old CSV files and update DB for the appropriate filename column and its old/update columns
                void DeleteOldCsvFilesAndUpdateDb(string folderPath, DateTime olderThan, string filenameColumn, string updateDateColumn, string oldFilenameColumn)
                {
                    if (!Directory.Exists(folderPath)) return;

                    var files = Directory.GetFiles(folderPath, "*.csv");
                    foreach (var file in files)
                    {
                        try
                        {
                            var fi = new FileInfo(file);
                            if (fi.CreationTime < olderThan)
                            {
                                string oldFileName = fi.Name;

                                string updateQuery = $@"
                                UPDATE tbl_usage_reports
                                SET
                                    row_update_date = GETDATE(),
                                    {updateDateColumn} = GETDATE(),
                                    {oldFilenameColumn} = {filenameColumn}
                                WHERE {filenameColumn} = @oldFileName";

                                using (SqlCommand cmd = new SqlCommand(updateQuery, conn))
                                {
                                    cmd.Parameters.AddWithValue("@oldFileName", oldFileName);
                                    int rows = cmd.ExecuteNonQuery();
                                    if (rows > 0) Console.WriteLine($"[DB UPDATED] Old file record updated -> {oldFileName}");
                                }

                                //if (deleteFile)
                                //{
                                    fi.Delete();
                                    Console.WriteLine($"[DELETED] {oldFileName}");
                                //}
                            }
                        }
                        catch (Exception ex)
                        {
                            try
                            {
                                string logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs", "Exceptions");
                                Directory.CreateDirectory(logDir);
                                string exLogFile = Path.Combine(logDir, $"ExceptionLog_{DateTime.Now:yyyyMMdd}.txt");
                                string logMessage = $@"
=============================
Date/Time : {DateTime.Now:yyyy-MM-dd HH:mm:ss}
File      : {file}
Error     : {ex.Message}
StackTrace: {ex.StackTrace}
=============================
";
                                File.AppendAllText(exLogFile, logMessage);
                                Console.WriteLine($"[ERROR] Logged exception for {file}: {ex.Message}");
                            }
                            catch (Exception logEx)
                            {
                                Console.WriteLine($"[FATAL] Could not write to log file: {logEx.Message}");
                            }
                        }
                    }
                }

                // Process resellers
                foreach (var reseller in resellers)
                {
                    // ===========================
                    // RESELLER-SPECIFIC CUSTOM FIELDS (field1..field10)
                    // Captions come from tbl_line_fields; only captioned fields are emitted,
                    // placed right after the "SOC Code" column. Values come from tbl_line_details.
                    // ===========================
                    var customFields = new List<(int Index, string Caption)>();
                    using (SqlCommand cmd = new SqlCommand(@"
SELECT TOP 1
    field1_caption, field2_caption, field3_caption, field4_caption, field5_caption,
    field6_caption, field7_caption, field8_caption, field9_caption, field10_caption
FROM tbl_line_fields
WHERE reseller_id = @resellerId", conn))
                    {
                        cmd.Parameters.AddWithValue("@resellerId", reseller.Id);
                        using (SqlDataReader rdr = cmd.ExecuteReader())
                        {
                            if (rdr.Read())
                            {
                                for (int i = 0; i < 10; i++)
                                {
                                    string caption = rdr.IsDBNull(i) ? null : rdr.GetString(i)?.Trim();
                                    if (!string.IsNullOrWhiteSpace(caption))
                                        customFields.Add((i + 1, caption));
                                }
                            }
                        }
                    }

                    // CSV-safe header captions, in column order.
                    var customFieldHeaders = customFields
                        .Select(f => "\"" + f.Caption.Replace("\"", "\"\"") + "\"")
                        .ToList();

                    // Read the active custom-field values for the current row into the per-key map.
                    void CaptureCustomFields(SqlDataReader dr, Dictionary<string, string[]> map, string key)
                    {
                        var vals = new string[customFields.Count];
                        for (int i = 0; i < customFields.Count; i++)
                        {
                            string col = "field" + customFields[i].Index;
                            vals[i] = dr[col] != DBNull.Value ? dr[col].ToString() : "";
                        }
                        map[key] = vals;
                    }

                    // CSV-safe custom-field values for the given key, in column order.
                    List<string> CustomFieldValues(Dictionary<string, string[]> map, string key)
                    {
                        var vals = new List<string>(customFields.Count);
                        string[] arr = map.ContainsKey(key) ? map[key] : null;
                        for (int i = 0; i < customFields.Count; i++)
                        {
                            string v = (arr != null && i < arr.Length) ? arr[i] : "";
                            vals.Add("\"" + (v ?? "").Replace("\"", "\"\"") + "\"");
                        }
                        return vals;
                    }

                    // Determine billing cycle using DB scalar functions
                    DateTime billCycleStart;
                    DateTime billCycleEnd;
                    using (SqlCommand cmd = new SqlCommand("SELECT dbo.fn_billing_start_date(@day, @today)", conn))
                    {
                        cmd.Parameters.AddWithValue("@day", reseller.StartDay);
                        cmd.Parameters.AddWithValue("@today", today);
                        var obj = cmd.ExecuteScalar();
                        billCycleStart = obj == null || obj == DBNull.Value ? new DateTime(today.Year, today.Month, 1) : Convert.ToDateTime(obj);
                    }
                    using (SqlCommand cmd = new SqlCommand("SELECT dbo.fn_billing_end_date(@day, @today)", conn))
                    {
                        cmd.Parameters.AddWithValue("@day", reseller.StartDay);
                        cmd.Parameters.AddWithValue("@today", today);
                        var obj = cmd.ExecuteScalar();
                        billCycleEnd = obj == null || obj == DBNull.Value ? billCycleStart.AddMonths(1).AddDays(-1) : Convert.ToDateTime(obj);
                    }

                    DateTime prevCycleStart = billCycleStart.AddMonths(-1);
                    DateTime prevCycleEnd = billCycleEnd.AddMonths(-1);


                    // Check if reseller has any usage data for month cycle
                    bool hasDataForMonth = false;

                    using (SqlCommand cmd = new SqlCommand(@"
    SELECT COUNT(*)
    FROM tbl_usage_reports
    WHERE reseller_id = @resellerId
      AND bill_cycle_start_date = @start
      AND bill_cycle_end_date = @end
", conn))
                    {
                        cmd.Parameters.AddWithValue("@resellerId", reseller.Id);
                        cmd.Parameters.AddWithValue("@start", billCycleStart);
                        cmd.Parameters.AddWithValue("@end", billCycleEnd);

                        int count = Convert.ToInt32(cmd.ExecuteScalar());
                        hasDataForMonth = count > 0;
                    }


                    bool isBillingStartDay = today.Date == billCycleStart.Date;

                    // ---------------------------
                    // Handle Billing Start Day (create billed CSVs for previous cycle and blank unbilled for next cycle)
                    // ---------------------------
                    if (isBillingStartDay && !hasDataForMonth)
                    {
                        Log($"Starting New Billing Cycle for reseller {reseller.Id} - {reseller.Name}");

                        // Close previous cycles globally
                        using (SqlCommand cmd = new SqlCommand($"UPDATE tbl_usage_reports SET is_current_bill_cycle = 0 WHERE is_current_bill_cycle = 1 AND reseller_id = {reseller.Id}", conn))
                        {
                            cmd.ExecuteNonQuery();
                        }

                        // === Delete last unbilled DATA file ===
                        string lastUnbilledDataFile = null;
                        using (SqlCommand cmd = new SqlCommand(@"
SELECT TOP 1 data_usage_filename 
FROM tbl_usage_reports 
WHERE reseller_id = @resellerId 
ORDER BY bill_cycle_start_date DESC", conn))
                        {
                            cmd.Parameters.AddWithValue("@resellerId", reseller.Id);
                            object result = cmd.ExecuteScalar();
                            if (result != null && result != DBNull.Value)
                                lastUnbilledDataFile = result.ToString();
                        }

                        if (!string.IsNullOrEmpty(lastUnbilledDataFile))
                        {
                            string lastUnbilledDataPath = Path.Combine(dataUnbilledPath, lastUnbilledDataFile);
                            if (File.Exists(lastUnbilledDataPath))
                            {
                                File.Delete(lastUnbilledDataPath);
                                Log($"[DELETE] Last unbilled DATA file deleted → {lastUnbilledDataPath}");
                                Console.WriteLine($"[DELETE] Last unbilled DATA file deleted → {lastUnbilledDataPath}");
                            }
                            else
                            {
                                Log($"[DELETE] Last unbilled DATA file not found → {lastUnbilledDataPath}");
                            }
                        }

                        // === Delete last unbilled SMS file ===
                        string lastUnbilledSmsFile = null;
                        using (SqlCommand cmd = new SqlCommand(@"
SELECT TOP 1 text_usage_filename 
FROM tbl_usage_reports 
WHERE reseller_id = @resellerId 
ORDER BY bill_cycle_start_date DESC", conn))
                        {
                            cmd.Parameters.AddWithValue("@resellerId", reseller.Id);
                            object result = cmd.ExecuteScalar();
                            if (result != null && result != DBNull.Value)
                                lastUnbilledSmsFile = result.ToString();
                        }

                        if (!string.IsNullOrEmpty(lastUnbilledSmsFile))
                        {
                            string lastUnbilledSmsPath = Path.Combine(smsUnbilledPath, lastUnbilledSmsFile);
                            if (File.Exists(lastUnbilledSmsPath))
                            {
                                File.Delete(lastUnbilledSmsPath);
                                Log($"[DELETE] Last unbilled SMS file deleted → {lastUnbilledSmsPath}");
                                Console.WriteLine($"[DELETE] Last unbilled SMS file deleted → {lastUnbilledSmsPath}");
                            }
                            else
                            {
                                Log($"[DELETE] Last unbilled SMS file not found → {lastUnbilledSmsPath}");
                            }
                        }

                        // ---------- End STEP: Delete last unbilled files (Data + SMS) ----------
                        // ---------- DATA: Billed CSV for previous cycle ----------
                        string billedDataFile = lastUnbilledDataFile == null ? $"DataUsage_{reseller.Id}_{rnd.Next(10000, 99999)}_{prevCycleStart:MMddyyyy}_{prevCycleEnd:MMddyyyy}.csv" : lastUnbilledDataFile;
                        //string billedDataFile = lastUnbilledDataFile;
                        string billedDataFilePath = Path.Combine(dataBilledPath, billedDataFile);

                        // ---------- SMS: Billed CSV for previous cycle (single declaration, no duplicate) ----------
                        string billedSmsFileForPrev = lastUnbilledSmsFile == null ? $"SMSUsage_{reseller.Id}_{rnd.Next(10000, 99999)}_{prevCycleStart:MMddyyyy}_{prevCycleEnd:MMddyyyy}.csv" : lastUnbilledSmsFile;
                        //string billedSmsFileForPrev = lastUnbilledSmsFile;
                        string billedSmsFilePathForPrev = Path.Combine(smsBilledPath, billedSmsFileForPrev);

                        var prevDateColumns = new List<string>();
                        for (DateTime dt = prevCycleStart; dt <= prevCycleEnd; dt = dt.AddDays(1))
                            prevDateColumns.Add(dt.ToString("MM-dd-yyyy", CultureInfo.InvariantCulture));

                        var billedDataHeaders = new List<string> { "BAN", "MSISDN", "SIM", "Assign To", "SOC Code" };
                        billedDataHeaders.AddRange(customFieldHeaders);
                        billedDataHeaders.AddRange(prevDateColumns);
                        billedDataHeaders.Add("Total Data in KB");
                        billedDataHeaders.Add("Total Data in MB");

                        var billedDataLines = new List<string> { string.Join(",", billedDataHeaders) };

                        string csvQueryData = @"
SELECT 
    b.ban_no AS BAN, 
    du.msisdn, 
    isnull(ld.alias,'') AS assignTo, 
    p.plan_code AS socCode, 
    du.usage_mb AS usage_kb, 
    du.line_detail_id, 
    du.usage_date,
    ld.SIM,
    ld.field1, ld.field2, ld.field3, ld.field4, ld.field5,
    ld.field6, ld.field7, ld.field8, ld.field9, ld.field10
FROM tbl_daily_usage du
JOIN tbl_resellers r ON du.reseller_id = r.reseller_id
JOIN tbl_line_details ld ON du.line_detail_id = ld.line_detail_id
JOIN tbl_plans p ON ld.plan_id = p.plan_id
LEFT JOIN tbl_bans b ON ld.ban_id = b.ban_id
WHERE du.reseller_id = @resellerId
  AND du.usage_date BETWEEN @start AND @end
ORDER BY b.ban_no, du.msisdn, du.usage_date";

                        var rowMapData = new Dictionary<string, Dictionary<string, decimal>>();
                        var assignMapData = new Dictionary<string, string>();
                        var socMapData = new Dictionary<string, string>();
                        var simMapData = new Dictionary<string, string>();
                        var fieldMapData = new Dictionary<string, string[]>();

                        using (SqlCommand cmd = new SqlCommand(csvQueryData, conn))
                        {
                            cmd.Parameters.AddWithValue("@resellerId", reseller.Id);
                            cmd.Parameters.AddWithValue("@start", prevCycleStart);
                            cmd.Parameters.AddWithValue("@end", prevCycleEnd);
                            using (SqlDataReader dr = cmd.ExecuteReader())
                            {
                                while (dr.Read())
                                {
                                    string ban = dr["BAN"] != DBNull.Value ? dr["BAN"].ToString() : "";
                                    string msisdn = dr["msisdn"] != DBNull.Value ? dr["msisdn"].ToString() : "";
                                    string sim = dr["SIM"] != DBNull.Value ? dr["SIM"].ToString() : "";
                                    string assignTo = dr["assignTo"] != DBNull.Value ? dr["assignTo"].ToString() : "";
                                    string socCode = dr["socCode"] != DBNull.Value ? dr["socCode"].ToString() : "";
                                    decimal usageKB = dr["usage_kb"] != DBNull.Value ? Convert.ToDecimal(dr["usage_kb"]) : 0;
                                    int lineDetailId = dr["line_detail_id"] != DBNull.Value ? Convert.ToInt32(dr["line_detail_id"]) : 0;
                                    DateTime usageDate = dr.GetDateTime(dr.GetOrdinal("usage_date"));

                                    string dateStr = usageDate.ToString("MM-dd-yyyy", CultureInfo.InvariantCulture);
                                    string key = $"{ban}_{lineDetailId}_{msisdn}";

                                    if (!rowMapData.ContainsKey(key)) rowMapData[key] = new Dictionary<string, decimal>();
                                    if (!rowMapData[key].ContainsKey(dateStr)) rowMapData[key][dateStr] = 0;
                                    rowMapData[key][dateStr] += usageKB;

                                    assignMapData[key] = assignTo;
                                    socMapData[key] = socCode;
                                    simMapData[key] = sim;
                                    CaptureCustomFields(dr, fieldMapData, key);
                                }
                            }
                        }

                        foreach (var kvp in rowMapData)
                        {
                            string key = kvp.Key;
                            var usageDict = kvp.Value;
                            var parts = key.Split('_');

                            var rowData = new List<string>
                        {
                            parts.Length > 0 ? parts[0] : "",
                            $"\"{(parts.Length > 2 ? parts[2] : "")}\"",
                            simMapData.ContainsKey(key) ? simMapData[key] : "",
                            $"\"{(assignMapData.ContainsKey(key) ? assignMapData[key] : "")}\"",
                            socMapData.ContainsKey(key) ? socMapData[key] : ""
                        };

                            rowData.AddRange(CustomFieldValues(fieldMapData, key));

                            decimal totalKB = 0;
                            foreach (var date in prevDateColumns)
                            {
                                decimal usage = usageDict.ContainsKey(date) ? usageDict[date] : 0;
                                rowData.Add(usage.ToString("F2", CultureInfo.InvariantCulture));
                                totalKB += usage;
                            }

                            rowData.Add(totalKB.ToString("F2", CultureInfo.InvariantCulture));
                            rowData.Add((totalKB / 1024m).ToString("F2", CultureInfo.InvariantCulture));
                            billedDataLines.Add(string.Join(",", rowData));
                        }

                        File.WriteAllLines(billedDataFilePath, billedDataLines);
                        Log($"[BILLED-DATA] Final billed file created → {billedDataFilePath}");
                        Console.WriteLine($"[BILLED-DATA] Final billed file created → {billedDataFilePath}");

                        // ---------- DATA: Create blank unbilled CSV for new cycle ----------
                        string unbilledDataFile = $"DataUsage_{reseller.Id}_{rnd.Next(10000, 99999)}_{billCycleStart:MMddyyyy}_{billCycleEnd:MMddyyyy}.csv";
                        string unbilledDataFilePath = Path.Combine(dataUnbilledPath, unbilledDataFile);

                        string unbilledSmsFile = $"SMSUsage_{reseller.Id}_{rnd.Next(10000, 99999)}_{billCycleStart:MMddyyyy}_{billCycleEnd:MMddyyyy}.csv";
                        string unbilledSmsFilePath = Path.Combine(smsUnbilledPath, unbilledSmsFile);

                        var headerData = new List<string> { "BAN", "MSISDN", "SIM", "Assign To", "SOC Code" };
                        headerData.AddRange(customFieldHeaders);
                        var newDateColumns = new List<string>();
                        DateTime dateEndForBlank = today;
                        for (DateTime dt = billCycleStart; dt <= dateEndForBlank; dt = dt.AddDays(1))
                            newDateColumns.Add(dt.ToString("MM-dd-yyyy", CultureInfo.InvariantCulture));
                        headerData.AddRange(newDateColumns);
                        headerData.Add("Total Data in KB");
                        headerData.Add("Total Data in MB");

                        File.WriteAllText(unbilledDataFilePath, string.Join(",", headerData) + Environment.NewLine);
                        Console.WriteLine($"[UNBILLED-DATA] New blank unbilled file → {unbilledDataFilePath}");

                        // Insert new unbilled cycle row for Data & SMS into tbl_usage_reports
                        string insertDataReport = @"
                         INSERT INTO tbl_usage_reports
                        (row_create_date, bill_cycle_start_date, bill_cycle_end_date, is_current_bill_cycle, reseller_id, data_usage_filename, text_usage_filename)
                        VALUES (GETDATE(), @start, @end, 1, @resellerId, @dataFile, @smsFile)";
                        using (SqlCommand cmd = new SqlCommand(insertDataReport, conn))
                        {
                            cmd.Parameters.AddWithValue("@start", billCycleStart);
                            cmd.Parameters.AddWithValue("@end", billCycleEnd);
                            cmd.Parameters.AddWithValue("@resellerId", reseller.Id);
                            cmd.Parameters.AddWithValue("@dataFile", unbilledDataFile);
                            cmd.Parameters.AddWithValue("@smsFile", unbilledSmsFile);
                            cmd.ExecuteNonQuery();
                        }

                        // ---------- SMS: Billed CSV for previous cycle (populate similar to Data) ----------
                        var billedSmsHeaders = new List<string> { "BAN", "MSISDN", "SIM", "Assign To", "SOC Code" };
                        billedSmsHeaders.AddRange(customFieldHeaders);
                        billedSmsHeaders.AddRange(prevDateColumns);
                        billedSmsHeaders.Add("Total SMSs");
                        var billedSmsLines = new List<string> { string.Join(",", billedSmsHeaders) };

                        string csvQuerySms = @"
SELECT 
    b.ban_no AS BAN, 
    du.msisdn, 
    isnull(ld.alias,'') AS assignTo, 
    p.plan_code AS socCode, 
    du.dom_sms AS usage_sms, 
    du.line_detail_id, 
    du.usage_date,
    ld.SIM,
    ld.field1, ld.field2, ld.field3, ld.field4, ld.field5,
    ld.field6, ld.field7, ld.field8, ld.field9, ld.field10
FROM tbl_daily_usage du
JOIN tbl_resellers r ON du.reseller_id = r.reseller_id
JOIN tbl_line_details ld ON du.line_detail_id = ld.line_detail_id
JOIN tbl_plans p ON ld.plan_id = p.plan_id
LEFT JOIN tbl_bans b ON ld.ban_id = b.ban_id
WHERE du.reseller_id = @resellerId
  AND du.usage_date BETWEEN @start AND @end
ORDER BY b.ban_no, du.msisdn, du.usage_date";

                        var rowMapSms = new Dictionary<string, Dictionary<string, decimal>>();
                        var assignMapSms = new Dictionary<string, string>();
                        var socMapSms = new Dictionary<string, string>();
                        var simMapSms = new Dictionary<string, string>();
                        var fieldMapSms = new Dictionary<string, string[]>();

                        using (SqlCommand cmd = new SqlCommand(csvQuerySms, conn))
                        {
                            cmd.Parameters.AddWithValue("@resellerId", reseller.Id);
                            cmd.Parameters.AddWithValue("@start", prevCycleStart);
                            cmd.Parameters.AddWithValue("@end", prevCycleEnd);

                            using (SqlDataReader dr = cmd.ExecuteReader())
                            {
                                while (dr.Read())
                                {
                                    string ban = dr["BAN"] != DBNull.Value ? dr["BAN"].ToString() : "";
                                    string msisdn = dr["msisdn"] != DBNull.Value ? dr["msisdn"].ToString() : "";
                                    string sim = dr["SIM"] != DBNull.Value ? dr["SIM"].ToString() : "";
                                    string assignTo = dr["assignTo"] != DBNull.Value ? dr["assignTo"].ToString() : "";
                                    string socCode = dr["socCode"] != DBNull.Value ? dr["socCode"].ToString() : "";
                                    decimal smsCount = dr["usage_sms"] != DBNull.Value ? Convert.ToDecimal(dr["usage_sms"]) : 0;
                                    int lineDetailId = dr["line_detail_id"] != DBNull.Value ? Convert.ToInt32(dr["line_detail_id"]) : 0;
                                    DateTime usageDate = dr.GetDateTime(dr.GetOrdinal("usage_date"));

                                    string dateStr = usageDate.ToString("MM-dd-yyyy", CultureInfo.InvariantCulture);
                                    string key = $"{ban}_{lineDetailId}_{msisdn}";

                                    if (!rowMapSms.ContainsKey(key)) rowMapSms[key] = new Dictionary<string, decimal>();
                                    if (!rowMapSms[key].ContainsKey(dateStr)) rowMapSms[key][dateStr] = 0;
                                    rowMapSms[key][dateStr] += smsCount;

                                    assignMapSms[key] = assignTo;
                                    socMapSms[key] = socCode;
                                    simMapSms[key] = sim;
                                    CaptureCustomFields(dr, fieldMapSms, key);
                                }
                            }
                        }

                        foreach (var kvp in rowMapSms)
                        {
                            string key = kvp.Key;
                            var usageDict = kvp.Value;
                            var parts = key.Split('_');

                            var rowData = new List<string>
                        {
                            parts.Length > 0 ? parts[0] : "",
                            $"\"{(parts.Length > 2 ? parts[2] : "")}\"",
                            simMapSms.ContainsKey(key) ? simMapSms[key] : "",
                            $"\"{(assignMapSms.ContainsKey(key) ? assignMapSms[key] : "")}\"",
                            socMapSms.ContainsKey(key) ? socMapSms[key] : ""
                        };

                            rowData.AddRange(CustomFieldValues(fieldMapSms, key));

                            decimal totalSMS = 0;
                            foreach (var date in prevDateColumns)
                            {
                                decimal sms = usageDict.ContainsKey(date) ? usageDict[date] : 0;
                                rowData.Add(sms.ToString("F0", CultureInfo.InvariantCulture));
                                totalSMS += sms;
                            }

                            rowData.Add(totalSMS.ToString("F0", CultureInfo.InvariantCulture));
                            billedSmsLines.Add(string.Join(",", rowData));
                        }

                        File.WriteAllLines(billedSmsFilePathForPrev, billedSmsLines);
                        Log($"[BILLED-SMS] Final billed SMS file created → {billedSmsFilePathForPrev}");
                        Console.WriteLine($"[BILLED-SMS] Final billed SMS file created → {billedSmsFilePathForPrev}");

                        // ---------- SMS: blank unbilled CSV for new cycle ----------
                        var headerSms = new List<string> { "BAN", "MSISDN", "SIM", "Assign To", "SOC Code" };
                        headerSms.AddRange(customFieldHeaders);
                        var newDateColumnsSms = new List<string>();
                        DateTime dateEndSms = today;
                        for (DateTime dt = billCycleStart; dt <= dateEndSms; dt = dt.AddDays(1))
                            newDateColumnsSms.Add(dt.ToString("MM-dd-yyyy", CultureInfo.InvariantCulture));
                        headerSms.AddRange(newDateColumnsSms);
                        headerSms.Add("Total SMSs");

                        File.WriteAllText(unbilledSmsFilePath, string.Join(",", headerSms) + Environment.NewLine);
                        Console.WriteLine($"[UNBILLED-SMS] New blank unbilled file → {unbilledSmsFilePath}");

                        // Update DB for billed paths (no file deletion); delete old unbilled files
                        //DeleteOldCsvFilesAndUpdateDb(dataBilledPath, prevCycleEnd.AddDays(1), "data_usage_filename", "data_usage_filename_update_date", "old_data_usage_filename", deleteFile: false);
                        DeleteOldCsvFilesAndUpdateDb(dataUnbilledPath, DateTime.Today, "data_usage_filename", "data_usage_filename_update_date", "old_data_usage_filename");
                       // DeleteOldCsvFilesAndUpdateDb(smsBilledPath, prevCycleEnd.AddDays(1), "text_usage_filename", "text_usage_filename_update_date", "old_text_usage_filename", deleteFile: false);
                        DeleteOldCsvFilesAndUpdateDb(smsUnbilledPath, DateTime.Today, "text_usage_filename", "text_usage_filename_update_date", "old_text_usage_filename");

                        Log("New Billing Cycle Created Successfully for reseller " + reseller.Name);
                        Log("Process End");
                        continue; // next reseller
                    } // end billing start day

                    // ==========================================================
                    // NORMAL DAY: create/update unbilled CSVs for Data and SMS (daily rotating)
                    // ==========================================================
                    // --- DATA: collect usage for cycle so far (billCycleStart..lastDate) ---
                    DateTime lastDate = (today.AddDays(-1) < billCycleEnd) ? today.AddDays(-1) : billCycleEnd;
                    var dataDateColumns = new List<string>();
                    for (DateTime dt = billCycleStart; dt <= lastDate; dt = dt.AddDays(1))
                        dataDateColumns.Add(dt.ToString("MM-dd-yyyy", CultureInfo.InvariantCulture));

                    string csvQueryNormalData = @"
SELECT 
    b.ban_no AS BAN, 
    du.msisdn, 
    isnull(ld.alias,'') AS assignTo, 
    p.plan_code AS socCode, 
    du.usage_mb AS usage_kb, 
    du.line_detail_id, 
    du.usage_date,
    ld.SIM,
    ld.field1, ld.field2, ld.field3, ld.field4, ld.field5,
    ld.field6, ld.field7, ld.field8, ld.field9, ld.field10
FROM tbl_daily_usage du
JOIN tbl_resellers r ON du.reseller_id = r.reseller_id
JOIN tbl_line_details ld ON du.line_detail_id = ld.line_detail_id
JOIN tbl_plans p ON ld.plan_id = p.plan_id
LEFT JOIN tbl_bans b ON ld.ban_id = b.ban_id
WHERE du.reseller_id = @resellerId
  AND du.usage_date BETWEEN @start AND @end
ORDER BY b.ban_no, du.msisdn, du.usage_date";

                    var rowMapNormal = new Dictionary<string, Dictionary<string, decimal>>();
                    var assignMapNormal = new Dictionary<string, string>();
                    var socMapNormal = new Dictionary<string, string>();
                    var simMapNormal = new Dictionary<string, string>();
                    var fieldMapNormal = new Dictionary<string, string[]>();

                    using (SqlCommand cmd = new SqlCommand(csvQueryNormalData, conn))
                    {
                        cmd.Parameters.AddWithValue("@resellerId", reseller.Id);
                        cmd.Parameters.AddWithValue("@start", billCycleStart);
                        cmd.Parameters.AddWithValue("@end", billCycleEnd);
                        using (SqlDataReader dr = cmd.ExecuteReader())
                        {
                            while (dr.Read())
                            {
                                string ban = dr["BAN"]?.ToString() ?? "";
                                string msisdn = dr["msisdn"]?.ToString() ?? "";
                                string sim = dr["SIM"]?.ToString() ?? "";
                                string assignTo = dr["assignTo"]?.ToString() ?? "";
                                string socCode = dr["socCode"]?.ToString() ?? "";
                                decimal usageKB = dr["usage_kb"] != DBNull.Value ? Convert.ToDecimal(dr["usage_kb"]) : 0;
                                int lineDetailId = dr["line_detail_id"] != DBNull.Value ? Convert.ToInt32(dr["line_detail_id"]) : 0;
                                DateTime usageDate = dr.GetDateTime(dr.GetOrdinal("usage_date"));
                                string dateStr = usageDate.ToString("MM-dd-yyyy", CultureInfo.InvariantCulture);
                                string key = $"{ban}_{lineDetailId}_{msisdn}";

                                if (!rowMapNormal.ContainsKey(key)) rowMapNormal[key] = new Dictionary<string, decimal>();
                                if (!rowMapNormal[key].ContainsKey(dateStr)) rowMapNormal[key][dateStr] = 0;
                                rowMapNormal[key][dateStr] += usageKB;

                                assignMapNormal[key] = assignTo;
                                socMapNormal[key] = socCode;
                                simMapNormal[key] = sim;
                                CaptureCustomFields(dr, fieldMapNormal, key);
                            }
                        }
                    }

                    // --- SMS: collect usage for cycle so far ---
                    var rowMapNormalSms = new Dictionary<string, Dictionary<string, decimal>>();
                    var assignMapNormalSms = new Dictionary<string, string>();
                    var socMapNormalSms = new Dictionary<string, string>();
                    var simMapNormalSms = new Dictionary<string, string>();
                    var fieldMapNormalSms = new Dictionary<string, string[]>();

                    string csvQueryNormalSms = @"
SELECT 
    b.ban_no AS BAN, 
    du.msisdn, 
    isnull(ld.alias,'') AS assignTo, 
    p.plan_code AS socCode, 
    du.dom_sms AS usage_sms, 
    du.line_detail_id, 
    du.usage_date,
    ld.SIM,
    ld.field1, ld.field2, ld.field3, ld.field4, ld.field5,
    ld.field6, ld.field7, ld.field8, ld.field9, ld.field10
FROM tbl_daily_usage du
JOIN tbl_resellers r ON du.reseller_id = r.reseller_id
JOIN tbl_line_details ld ON du.line_detail_id = ld.line_detail_id
JOIN tbl_plans p ON ld.plan_id = p.plan_id
LEFT JOIN tbl_bans b ON ld.ban_id = b.ban_id
WHERE du.reseller_id = @resellerId
  AND du.usage_date BETWEEN @start AND @end
ORDER BY b.ban_no, du.msisdn, du.usage_date";

                    using (SqlCommand cmd = new SqlCommand(csvQueryNormalSms, conn))
                    {
                        cmd.Parameters.AddWithValue("@resellerId", reseller.Id);
                        cmd.Parameters.AddWithValue("@start", billCycleStart);
                        cmd.Parameters.AddWithValue("@end", billCycleEnd);
                        using (SqlDataReader dr = cmd.ExecuteReader())
                        {
                            while (dr.Read())
                            {
                                string ban = dr["BAN"]?.ToString() ?? "";
                                string msisdn = dr["msisdn"]?.ToString() ?? "";
                                string sim = dr["SIM"]?.ToString() ?? "";
                                string assignTo = dr["assignTo"]?.ToString() ?? "";
                                string socCode = dr["socCode"]?.ToString() ?? "";
                                decimal usageSms = dr["usage_sms"] != DBNull.Value ? Convert.ToDecimal(dr["usage_sms"]) : 0;
                                int lineDetailId = dr["line_detail_id"] != DBNull.Value ? Convert.ToInt32(dr["line_detail_id"]) : 0;
                                DateTime usageDate = dr.GetDateTime(dr.GetOrdinal("usage_date"));
                                string dateStr = usageDate.ToString("MM-dd-yyyy", CultureInfo.InvariantCulture);
                                string key = $"{ban}_{lineDetailId}_{msisdn}";

                                if (!rowMapNormalSms.ContainsKey(key)) rowMapNormalSms[key] = new Dictionary<string, decimal>();
                                if (!rowMapNormalSms[key].ContainsKey(dateStr)) rowMapNormalSms[key][dateStr] = 0;
                                rowMapNormalSms[key][dateStr] += usageSms;

                                assignMapNormalSms[key] = assignTo;
                                socMapNormalSms[key] = socCode;
                                simMapNormalSms[key] = sim;
                                CaptureCustomFields(dr, fieldMapNormalSms, key);
                            }
                        }
                    }

                    // --- Find previous unbilled files (if any) ---
                    string[] existingDataFiles = Directory.Exists(dataUnbilledPath) ? Directory.GetFiles(dataUnbilledPath, $"DataUsage_{reseller.Id}_*.csv") : new string[0];
                    string prevDataFilePath = existingDataFiles.OrderByDescending(f => new FileInfo(f).CreationTime).FirstOrDefault();
                    string prevDataFileName = prevDataFilePath != null ? Path.GetFileName(prevDataFilePath) : null;

                    string[] existingSmsFiles = Directory.Exists(smsUnbilledPath) ? Directory.GetFiles(smsUnbilledPath, $"SMSUsage_{reseller.Id}_*.csv") : new string[0];
                    string prevSmsFilePath = existingSmsFiles.OrderByDescending(f => new FileInfo(f).CreationTime).FirstOrDefault();
                    string prevSmsFileName = prevSmsFilePath != null ? Path.GetFileName(prevSmsFilePath) : null;

                    // --- Always create new files for today (rotating approach) ---
                    string newDataFileName = $"DataUsage_{reseller.Id}_{rnd.Next(10000, 99999)}_{billCycleStart:MMddyyyy}_{billCycleEnd:MMddyyyy}_{today:MMddyyyy}.csv";
                    string newDataFilePath = Path.Combine(dataUnbilledPath, newDataFileName);

                    string newSmsFileName = $"SMSUsage_{reseller.Id}_{rnd.Next(10000, 99999)}_{billCycleStart:MMddyyyy}_{billCycleEnd:MMddyyyy}_{today:MMddyyyy}.csv";
                    string newSmsFilePath = Path.Combine(smsUnbilledPath, newSmsFileName);

                    // --- Build and write Data CSV ---
                    var dataHeaders = new List<string> { "BAN", "MSISDN", "SIM", "Assign To", "SOC Code" };
                    dataHeaders.AddRange(customFieldHeaders);
                    dataHeaders.AddRange(dataDateColumns);
                    dataHeaders.Add("Total Data in KB");
                    dataHeaders.Add("Total Data in MB");

                    var dataLines = new List<string> { string.Join(",", dataHeaders) };

                    foreach (var kvp in rowMapNormal)
                    {
                        string key = kvp.Key;
                        var usageDict = kvp.Value;
                        var parts = key.Split('_');

                        var rowData = new List<string>
                    {
                        parts.Length > 0 ? parts[0] : "",
                        $"\"{(parts.Length > 2 ? parts[2] : "")}\"",
                        simMapNormal.ContainsKey(key) ? simMapNormal[key] : "",
                        $"\"{(assignMapNormal.ContainsKey(key) ? assignMapNormal[key] : "")}\"",
                        socMapNormal.ContainsKey(key) ? socMapNormal[key] : ""
                    };

                        rowData.AddRange(CustomFieldValues(fieldMapNormal, key));

                        decimal totalKB = 0;
                        foreach (var date in dataDateColumns)
                        {
                            decimal usage = usageDict.ContainsKey(date) ? usageDict[date] : 0;
                            rowData.Add(usage.ToString("F2", CultureInfo.InvariantCulture));
                            totalKB += usage;
                        }

                        rowData.Add(totalKB.ToString("F2", CultureInfo.InvariantCulture));
                        rowData.Add((totalKB / 1024m).ToString("F2", CultureInfo.InvariantCulture));
                        dataLines.Add(string.Join(",", rowData));
                    }

                    File.WriteAllLines(newDataFilePath, dataLines);
                    Console.WriteLine($"[UPDATED-DATA] Unbilled CSV updated for reseller {reseller.Name}");
                    Log($"[UPDATED-DATA] Unbilled CSV updated for reseller {reseller.Name} - FullPath={newDataFilePath}");

                    // --- Build and write SMS CSV ---
                    var smsHeaders = new List<string> { "BAN", "MSISDN", "SIM", "Assign To", "SOC Code" };
                    smsHeaders.AddRange(customFieldHeaders);
                    smsHeaders.AddRange(dataDateColumns); // same date columns range
                    smsHeaders.Add("Total SMSs");

                    var smsLines = new List<string> { string.Join(",", smsHeaders) };

                    foreach (var kvp in rowMapNormalSms)
                    {
                        string key = kvp.Key;
                        var usageDict = kvp.Value;
                        var parts = key.Split('_');

                        var rowData = new List<string>
                    {
                        parts.Length > 0 ? parts[0] : "",
                        $"\"{(parts.Length > 2 ? parts[2] : "")}\"",
                        simMapNormalSms.ContainsKey(key) ? simMapNormalSms[key] : "",
                        $"\"{(assignMapNormalSms.ContainsKey(key) ? assignMapNormalSms[key] : "")}\"",
                        socMapNormalSms.ContainsKey(key) ? socMapNormalSms[key] : ""
                    };

                        rowData.AddRange(CustomFieldValues(fieldMapNormalSms, key));

                        decimal totalSMS = 0;
                        foreach (var date in dataDateColumns)
                        {
                            decimal usage = usageDict.ContainsKey(date) ? usageDict[date] : 0;
                            rowData.Add(usage.ToString("F0", CultureInfo.InvariantCulture));
                            totalSMS += usage;
                        }

                        rowData.Add(totalSMS.ToString("F0", CultureInfo.InvariantCulture));
                        smsLines.Add(string.Join(",", rowData));
                    }

                    File.WriteAllLines(newSmsFilePath, smsLines);
                    Console.WriteLine($"[UPDATED-SMS] Unbilled CSV updated for reseller {reseller.Name}");
                    Log($"[UPDATED-SMS] Unbilled CSV updated for reseller {reseller.Name} - {newSmsFileName}");

                    // --- Insert if not exists then update tbl_usage_reports (set both data & text filename columns) ---
                    string existsQuery = @"SELECT COUNT(1) FROM tbl_usage_reports WHERE reseller_id = @resellerId AND is_current_bill_cycle = 1";
                    int existsCount = 0;
                    using (SqlCommand cmd = new SqlCommand(existsQuery, conn))
                    {
                        cmd.Parameters.AddWithValue("@resellerId", reseller.Id);
                        existsCount = Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
                    }

                    if (existsCount == 0)
                    {
                        string insertQuery = @"
                        INSERT INTO tbl_usage_reports
                        (row_create_date, bill_cycle_start_date, bill_cycle_end_date, is_current_bill_cycle, reseller_id, data_usage_filename, text_usage_filename)
                        VALUES (GETDATE(), @start, @end, 1, @resellerId, @dataFile, @smsFile)";
                        using (SqlCommand cmd = new SqlCommand(insertQuery, conn))
                        {
                            cmd.Parameters.AddWithValue("@start", billCycleStart);
                            cmd.Parameters.AddWithValue("@end", billCycleEnd);
                            cmd.Parameters.AddWithValue("@resellerId", reseller.Id);
                            cmd.Parameters.AddWithValue("@dataFile", newDataFileName);
                            cmd.Parameters.AddWithValue("@smsFile", newSmsFileName);
                            cmd.ExecuteNonQuery();
                            Log($"[INSERTED] tbl_usage_reports inserted for ResellerID {reseller.Id}");
                        }
                    }
                    else
                    {
                        // Update both filename columns and move previous to old_* columns
                        string updateQuery = @"
                        UPDATE tbl_usage_reports
                        SET 
                            row_update_date = GETDATE(),
                            data_usage_filename_update_date = GETDATE(),
                            old_data_usage_filename = @oldData,
                            data_usage_filename = @newData,
                            text_usage_filename_update_date = GETDATE(),
                            old_text_usage_filename = @oldSms,
                            text_usage_filename = @newSms
                        WHERE reseller_id = @resellerId
                          AND is_current_bill_cycle = 1";

                        using (SqlCommand cmd = new SqlCommand(updateQuery, conn))
                        {
                            cmd.Parameters.AddWithValue("@oldData", prevDataFileName ?? "");
                            cmd.Parameters.AddWithValue("@newData", newDataFileName);
                            cmd.Parameters.AddWithValue("@oldSms", prevSmsFileName ?? "");
                            cmd.Parameters.AddWithValue("@newSms", newSmsFileName);
                            cmd.Parameters.AddWithValue("@resellerId", reseller.Id);
                            cmd.ExecuteNonQuery();
                            Log($"[UPDATED] tbl_usage_reports updated for ResellerID {reseller.Id}");
                        }
                    }

                    // Delete previous day's unbilled files (optional rotation)
                    if (!string.IsNullOrEmpty(prevDataFilePath) && File.Exists(prevDataFilePath))
                    {
                        try { File.Delete(prevDataFilePath); Console.WriteLine($"[DELETED] Old unbilled data file → {prevDataFileName}"); }
                        catch (Exception ex) { Log($"Error deleting old unbilled data file {prevDataFileName}: {ex.Message}"); }
                    }
                    if (!string.IsNullOrEmpty(prevSmsFilePath) && File.Exists(prevSmsFilePath))
                    {
                        try { File.Delete(prevSmsFilePath); Console.WriteLine($"[DELETED] Old unbilled SMS file → {prevSmsFileName}"); }
                        catch (Exception ex) { Log($"Error deleting old unbilled SMS file {prevSmsFileName}: {ex.Message}"); }
                    }

                    // -------------------------
                    // SMS DIFF: compare newSmsFilePath vs prevSmsFilePath (if prev exists)
                    // -------------------------
                    try
                    {
                        if (!string.IsNullOrEmpty(prevSmsFilePath) && File.Exists(prevSmsFilePath))
                        {
                            var prevTotals = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
                            foreach (var line in File.ReadLines(prevSmsFilePath).Skip(1))
                            {
                                if (string.IsNullOrWhiteSpace(line)) continue;
                                var cols = line.Split(',');
                                if (cols.Length < 2) continue;
                                string ban = cols[0].Trim();
                                string msisdn = cols.Length > 1 ? cols[1].Trim().Trim('"') : "";
                                string key = $"{ban}_{msisdn}";
                                decimal prevTotal = 0;
                                if (decimal.TryParse(cols.Last().Trim().Trim('"'), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedPrev))
                                    prevTotal = parsedPrev;
                                prevTotals[key] = prevTotal;
                            }

                            var diffs = new List<string>();
                            diffs.Add("BAN,MSISDN,SIM,Assign To,SOC Code,PrevTotalSMS,NewTotalSMS,Diff");

                            foreach (var line in File.ReadLines(newSmsFilePath).Skip(1))
                            {
                                if (string.IsNullOrWhiteSpace(line)) continue;
                                var cols = line.Split(',');
                                if (cols.Length < 2) continue;
                                string ban = cols[0].Trim();
                                string msisdn = cols.Length > 1 ? cols[1].Trim().Trim('"') : "";
                                string sim = cols.Length > 2 ? cols[2].Trim() : "";
                                string assignTo = cols.Length > 3 ? cols[3].Trim() : "";
                                string socCode = cols.Length > 4 ? cols[4].Trim() : "";
                                string key = $"{ban}_{msisdn}";

                                decimal newTotal = 0;
                                if (decimal.TryParse(cols.Last().Trim().Trim('"'), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedNew))
                                    newTotal = parsedNew;

                                prevTotals.TryGetValue(key, out var prevTotalVal);

                                if (prevTotalVal != newTotal)
                                {
                                    decimal diff = newTotal - prevTotalVal;
                                    diffs.Add($"{ban},{msisdn},{sim},{assignTo},{socCode},{prevTotalVal.ToString("F0", CultureInfo.InvariantCulture)},{newTotal.ToString("F0", CultureInfo.InvariantCulture)},{diff.ToString("F0", CultureInfo.InvariantCulture)}");
                                }
                            }

                            if (diffs.Count > 1)
                            {
                                string diffFileName = $"SMSUsageDiff_{reseller.Id}_{rnd.Next(10000, 99999)}_{billCycleStart:MMddyyyy}_{billCycleEnd:MMddyyyy}_{today:MMddyyyy}.csv";
                                string diffFilePath = Path.Combine(smsUnbilledPath, diffFileName);
                                File.WriteAllLines(diffFilePath, diffs);
                                Log($"[SMS-DIFF] Diff file created → {diffFilePath}");
                                Console.WriteLine($"[SMS-DIFF] Diff file created → {diffFilePath}");
                            }
                            else
                            {
                                Console.WriteLine("[SMS-DIFF] No changes vs previous unbilled SMS file.");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"Error creating SMS diff: {ex.Message}");
                    }

                    Log("Process End for reseller " + reseller.Name);
                } // end foreach reseller
            } // end using conn
        }
        catch (Exception ex)
        {
            try { File.AppendAllText(logFile, $"{DateTime.Now} ERROR: {ex}\n"); } catch { }
        }
    }
}
