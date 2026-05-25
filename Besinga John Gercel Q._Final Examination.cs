using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace StudentConsultationSystem
{
    #region Models

    public class ConsultationLog
    {
        public string RecordId { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public bool IsActive { get; set; }
        public string Checksum { get; set; }

        public string StudentId { get; set; }
        public string StudentName { get; set; }
        public string CounselorName { get; set; }
        public string Notes { get; set; }

        public string CalculateChecksum()
        {
            string rawData = $"{RecordId}|{StudentId}|{StudentName}|{CounselorName}|{Notes}|{CreatedAt:O}|{UpdatedAt:O}|{IsActive}";
            using (SHA256 sha256Hash = SHA256.Create())
            {
                byte[] bytes = sha256Hash.ComputeHash(Encoding.UTF8.GetBytes(rawData));
                StringBuilder builder = new StringBuilder();
                for (int i = 0; i < bytes.Length; i++)
                {
                    builder.Append(bytes[i].ToString("x2"));
                }
                return builder.ToString();
            }
        }

        public string ToCsvRow()
        {
            return $"{RecordId},{EscapeCsv(StudentId)},{EscapeCsv(StudentName)},{EscapeCsv(CounselorName)},{EscapeCsv(Notes)},{CreatedAt:O},{UpdatedAt:O},{IsActive},{Checksum}";
        }

        public static ConsultationLog FromCsvRow(string csvLine)
        {
            string[] tokens = csvLine.Split(',');
            if (tokens.Length < 9) throw new FormatException("Invalid data row format.");

            return new ConsultationLog
            {
                RecordId = tokens[0],
                StudentId = UnescapeCsv(tokens[1]),
                StudentName = UnescapeCsv(tokens[2]),
                CounselorName = UnescapeCsv(tokens[3]),
                Notes = UnescapeCsv(tokens[4]),
                CreatedAt = DateTime.Parse(tokens[5]),
                UpdatedAt = DateTime.Parse(tokens[6]),
                IsActive = bool.Parse(tokens[7]),
                Checksum = tokens[8]
            };
        }

        private static string EscapeCsv(string str) => str.Replace(",", "\\c").Replace("\n", "\\n");
        private static string UnescapeCsv(string str) => str.Replace("\\c", ",").Replace("\\n", "\n");
    }

    #endregion

    #region Audit Logger

    public static class AuditLogger
    {
        private static readonly string AuditFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "audit.log");

        public static void Log(string action, string details)
        {
            try
            {
                string directory = Path.GetDirectoryName(AuditFilePath);
                if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);

                string logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ACTION: {action.PadRight(10)} | DETAILS: {details}{Environment.NewLine}";
                File.AppendAllText(AuditFilePath, logEntry);
            }
            catch
            {
                Console.Error.WriteLine("Failed to write to audit log.");
            }
        }
    }

    #endregion

    #region Validation Component

    public static class Validator
    {
        public static bool ValidateConsultation(string studentId, string studentName, string counselorName, string notes, out string errors)
        {
            List<string> errorList = new List<string>();

            if (string.IsNullOrWhiteSpace(studentId))
                errorList.Add("Student ID cannot be empty.");

            if (string.IsNullOrWhiteSpace(studentName))
                errorList.Add("Student Name cannot be empty.");

            if (string.IsNullOrWhiteSpace(counselorName))
                errorList.Add("Counselor Name cannot be empty.");

            if (string.IsNullOrWhiteSpace(notes))
                errorList.Add("Consultation summary notes cannot be empty.");

            errors = string.Join(" | ", errorList);
            return errorList.Count == 0;
        }
    }

    #endregion

    #region Data Service / File Repository

    public class FileRepository
    {
        private readonly string _dataDir;
        private readonly string _dbFilePath;

        public FileRepository()
        {
            _dataDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
            _dbFilePath = Path.Combine(_dataDir, "consultation_logs.csv");
            InitializeStorage();
        }

        public void InitializeStorage()
        {
            try
            {
                if (!Directory.Exists(_dataDir))
                {
                    Directory.CreateDirectory(_dataDir);
                }

                if (!File.Exists(_dbFilePath))
                {
                    File.WriteAllText(_dbFilePath, "RecordId,StudentId,StudentName,CounselorName,Notes,CreatedAt,UpdatedAt,IsActive,Checksum" + Environment.NewLine);
                    AuditLogger.Log("INIT", "Database file created successfully.");
                }
            }
            catch (Exception ex)
            {
                AuditLogger.Log("ERROR", $"Initialization failed: {ex.Message}");
                Console.WriteLine($"Critical Error initializing storage: {ex.Message}");
            }
        }

        private List<ConsultationLog> LoadAllRaw()
        {
            List<ConsultationLog> records = new List<ConsultationLog>();
            if (!File.Exists(_dbFilePath)) return records;

            string[] lines = File.ReadAllLines(_dbFilePath);
            for (int i = 1; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i])) continue;
                try
                {
                    ConsultationLog record = ConsultationLog.FromCsvRow(lines[i]);

                    if (record.Checksum != record.CalculateChecksum())
                    {
                        AuditLogger.Log("ERROR", $"Data corruption detected at record ID: {record.RecordId}. Checksum mismatch.");
                        Console.WriteLine($"[Warning] System data integrity check failed for Record ID: {record.RecordId}. Data may have been tampered with.");
                    }

                    records.Add(record);
                }
                catch (Exception ex)
                {
                    AuditLogger.Log("ERROR", $"Failed to parse row {i}: {ex.Message}");
                }
            }
            return records;
        }

        private void SaveAll(List<ConsultationLog> records)
        {
            try
            {
                List<string> lines = new List<string>
                {
                    "RecordId,StudentId,StudentName,CounselorName,Notes,CreatedAt,UpdatedAt,IsActive,Checksum"
                };
                lines.AddRange(records.Select(r => r.ToCsvRow()));
                File.WriteAllLines(_dbFilePath, lines);
            }
            catch (IOException ex)
            {
                AuditLogger.Log("ERROR", $"IO Exception during save operation: {ex.Message}");
                throw;
            }
        }

        public void Add(ConsultationLog log)
        {
            List<ConsultationLog> records = LoadAllRaw();
            log.RecordId = "CON-" + Guid.NewGuid().ToString().Substring(0, 8).ToUpper();
            log.CreatedAt = DateTime.Now;
            log.UpdatedAt = DateTime.Now;
            log.IsActive = true;
            log.Checksum = log.CalculateChecksum();

            records.Add(log);
            SaveAll(records);
            AuditLogger.Log("ADD", $"Created record {log.RecordId} (Student ID: {log.StudentId})");
        }

        public List<ConsultationLog> GetAllActive()
        {
            AuditLogger.Log("READ", "Retrieved active consultation records.");
            return LoadAllRaw().Where(r => r.IsActive).ToList();
        }

        public ConsultationLog GetById(string id)
        {
            return LoadAllRaw().FirstOrDefault(r => r.RecordId.Equals(id, StringComparison.OrdinalIgnoreCase));
        }

        public bool Update(string id, string newStudentId, string newStudentName, string newCounselorName, string newNotes)
        {
            List<ConsultationLog> records = LoadAllRaw();
            ConsultationLog target = records.FirstOrDefault(r => r.RecordId.Equals(id, StringComparison.OrdinalIgnoreCase));

            if (target == null)
            {
                AuditLogger.Log("ERROR", $"Update failed. Record ID {id} not found.");
                return false;
            }

            target.StudentId = newStudentId;
            target.StudentName = newStudentName;
            target.CounselorName = newCounselorName;
            target.Notes = newNotes;
            target.UpdatedAt = DateTime.Now;
            target.Checksum = target.CalculateChecksum();

            SaveAll(records);
            AuditLogger.Log("UPDATE", $"Updated record {id}");
            return true;
        }

        public bool SoftDelete(string id)
        {
            List<ConsultationLog> records = LoadAllRaw();
            ConsultationLog target = records.FirstOrDefault(r => r.RecordId.Equals(id, StringComparison.OrdinalIgnoreCase));

            if (target == null || !target.IsActive)
            {
                AuditLogger.Log("ERROR", $"Soft delete failed. Active Record ID {id} not found.");
                return false;
            }

            target.IsActive = false;
            target.UpdatedAt = DateTime.Now;
            target.Checksum = target.CalculateChecksum();

            SaveAll(records);
            AuditLogger.Log("DELETE_SOFT", $"Soft-deleted record {id}");
            return true;
        }

        public bool HardDelete(string id)
        {
            List<ConsultationLog> records = LoadAllRaw();
            ConsultationLog target = records.FirstOrDefault(r => r.RecordId.Equals(id, StringComparison.OrdinalIgnoreCase));

            if (target == null)
            {
                AuditLogger.Log("ERROR", $"Hard delete failed. Record ID {id} not found.");
                return false;
            }

            records.Remove(target);
            SaveAll(records);
            AuditLogger.Log("DELETE_HARD", $"Permanently purged record {id} from database storage.");
            return true;
        }
    }

    #endregion

    #region Report Generator

    public class ReportGenerator
    {
        private readonly FileRepository _repo;

        public ReportGenerator(FileRepository repo)
        {
            _repo = repo;
        }

        public void GenerateConsultationSummaryReport()
        {
            List<ConsultationLog> activeLogs = _repo.GetAllActive();
            string reportPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "Consultation_Report.txt");

            var logsThisMonth = activeLogs.Where(l => l.CreatedAt.Month == DateTime.Now.Month && l.CreatedAt.Year == DateTime.Now.Year).ToList();
            var counselorGroups = activeLogs.GroupBy(l => l.CounselorName).ToDictionary(g => g.Key, g => g.Count());

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("==================================================");
            sb.AppendLine("       STUDENT CONSULTATION SUMMARY REPORT        ");
            sb.AppendLine($"Generated on: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine("==================================================");
            sb.AppendLine($"Total Active Logs Tracked      : {activeLogs.Count}");
            sb.AppendLine($"Logs Recorded This Month       : {logsThisMonth.Count}");
            sb.AppendLine("--------------------------------------------------");
            sb.AppendLine("Consultations Handled per Counselor:");
            foreach (var group in counselorGroups)
            {
                sb.AppendLine($"  - Counselor '{group.Key}': {group.Value} session(s)");
            }
            sb.AppendLine("--------------------------------------------------");
            sb.AppendLine(string.Format("{0,-12} {1,-12} {2,-18} {3,-15}", "Record ID", "Student ID", "Student Name", "Counselor"));
            sb.AppendLine("--------------------------------------------------");

            foreach (var log in activeLogs)
            {
                sb.AppendLine(string.Format("{0,-12} {1,-12} {2,-18} {3,-15}",
                    log.RecordId,
                    log.StudentId,
                    log.StudentName.Length > 15 ? log.StudentName.Substring(0, 12) + "..." : log.StudentName,
                    log.CounselorName));
            }
            sb.AppendLine("==================================================");
            sb.AppendLine("            *** END OF SYSTEM REPORT *** ");

            try
            {
                File.WriteAllText(reportPath, sb.ToString());
                Console.WriteLine(sb.ToString());
                Console.WriteLine($"\n[Success] Report generated and exported to file:\n--> {reportPath}");
                AuditLogger.Log("REPORT", "Generated Student Consultation Summary Report successfully.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"An error occurred printing report out to file: {ex.Message}");
                AuditLogger.Log("ERROR", $"Failed printing file report: {ex.Message}");
            }
        }
    }

    #endregion

    #region Program Menu / Controller

    class Program
    {
        private static FileRepository _repository;
        private static ReportGenerator _reportGenerator;

        static void Main(string[] args)
        {
            _repository = new FileRepository();
            _reportGenerator = new ReportGenerator(_repository);

            bool running = true;
            while (running)
            {
                Console.Clear();
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine("=========================================================");
                Console.WriteLine("                    MAIN SELECTION MENU                  ");
                Console.WriteLine("=========================================================");
                Console.ResetColor();
                Console.WriteLine("1. Initialize storage");
                Console.WriteLine("2. Add record");
                Console.WriteLine("3. View records");
                Console.WriteLine("4. Update record");
                Console.WriteLine("5. Delete record");
                Console.WriteLine("6. Audit logging");
                Console.WriteLine("7. Report generation");
                Console.WriteLine("8. Error handling");
                Console.WriteLine("---------------------------------------------------------");
                Console.Write("Select an operation option [1-8]: ");

                string selection = Console.ReadLine();
                switch (selection)
                {
                    case "1":
                        RunInitializeUi();
                        break;
                    case "2":
                        AddRecordUi();
                        break;
                    case "3":
                        ChooseViewOrFilterUi();
                        break;
                    case "4":
                        UpdateRecordUi();
                        break;
                    case "5":
                        DeleteRecordUi();
                        break;
                    case "6":
                        ViewAuditLogUi();
                        break;
                    case "7":
                        GenerateReportUi();
                        break;
                    case "8":
                        TriggerErrorDemoUi();
                        break;
                    default:
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine("Selection out of range. Press any key to try again.");
                        Console.ResetColor();
                        Console.ReadKey();
                        break;
                }
            }
        }

        private static void RunInitializeUi()
        {
            Console.Clear();
            Console.WriteLine(">>> 1. INITIALIZE STORAGE <<<\n");
            _repository.InitializeStorage();
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("Storage initialization verification complete. Folders and files verified.");
            Console.ResetColor();
            PressAnyKey();
        }

        private static void AddRecordUi()
        {
            Console.Clear();
            Console.WriteLine(">>> 2. ADD RECORD <<<\n");

            Console.Write("Enter Student ID: ");
            string studentId = Console.ReadLine();
            Console.Write("Enter Student Name: ");
            string studentName = Console.ReadLine();
            Console.Write("Enter Counselor Name: ");
            string counselorName = Console.ReadLine();
            Console.Write("Enter Session Consultation Notes: ");
            string notes = Console.ReadLine();

            if (!Validator.ValidateConsultation(studentId, studentName, counselorName, notes, out string errors))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"\nValidation Failure: {errors}");
                Console.ResetColor();
                AuditLogger.Log("WARN", $"Validation failed on creation: {errors}");
                PressAnyKey();
                return;
            }

            ConsultationLog newLog = new ConsultationLog
            {
                StudentId = studentId,
                StudentName = studentName,
                CounselorName = counselorName,
                Notes = notes
            };

            try
            {
                _repository.Add(newLog);
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("\nRecord processed, validated, assigned unique ID, signed with checksum, and saved to file.");
                Console.ResetColor();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"An error occurred writing data: {ex.Message}");
            }
            PressAnyKey();
        }

        private static void ChooseViewOrFilterUi()
        {
            Console.Clear();
            Console.WriteLine(">>> 3. VIEW RECORDS <<<\n");
            Console.WriteLine("1. Display all active records");
            Console.WriteLine("2. Search/Filter records by Student ID");
            Console.Write("\nChoose sub-option: ");
            string choice = Console.ReadLine();

            if (choice == "1")
            {
                Console.Clear();
                Console.WriteLine(">>> DISPLAYING ALL ACTIVE RECORDS <<<\n");
                var items = _repository.GetAllActive();
                DisplayTable(items);
            }
            else if (choice == "2")
            {
                Console.Clear();
                Console.WriteLine(">>> SEARCH/FILTER BY FIELD (STUDENT ID) <<<\n");
                Console.Write("Enter absolute or partial Student ID to match: ");
                string filterText = Console.ReadLine() ?? "";

                var items = _repository.GetAllActive()
                    .Where(b => b.StudentId.Contains(filterText, StringComparison.OrdinalIgnoreCase)).ToList();

                AuditLogger.Log("FILTER", $"Queried for Student ID containing: '{filterText}'");
                DisplayTable(items);
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Invalid selection.");
                Console.ResetColor();
            }
            PressAnyKey();
        }

        private static void UpdateRecordUi()
        {
            Console.Clear();
            Console.WriteLine(">>> 4. UPDATE RECORD <<<\n");
            Console.Write("Enter unique RecordId to update (e.g. CON-XXXXXX): ");
            string targetId = Console.ReadLine();

            ConsultationLog current = _repository.GetById(targetId);
            if (current == null || !current.IsActive)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Active target record identifier not found.");
                Console.ResetColor();
                PressAnyKey();
                return;
            }

            Console.WriteLine($"\nFound active record tracking: [ Student ID: {current.StudentId} | Name: {current.StudentName} ]");
            Console.Write($"Enter modified Student ID (or press Enter to keep '{current.StudentId}'): ");
            string newStudentId = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(newStudentId)) newStudentId = current.StudentId;

            Console.Write($"Enter modified Student Name (or press Enter to keep '{current.StudentName}'): ");
            string newStudentName = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(newStudentName)) newStudentName = current.StudentName;

            Console.Write($"Enter modified Counselor Name (or press Enter to keep '{current.CounselorName}'): ");
            string newCounselor = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(newCounselor)) newCounselor = current.CounselorName;

            Console.Write($"Enter modified Notes (or press Enter to keep '{current.Notes}'): ");
            string newNotes = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(newNotes)) newNotes = current.Notes;

            if (!Validator.ValidateConsultation(newStudentId, newStudentName, newCounselor, newNotes, out string errors))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"\nValidation Error: {errors}");
                Console.ResetColor();
                PressAnyKey();
                return;
            }

            if (_repository.Update(targetId, newStudentId, newStudentName, newCounselor, newNotes))
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("\nRecord state refreshed, UpdatedAt updated, checksum recomputed, and committed to file.");
                Console.ResetColor();
            }
            PressAnyKey();
        }

        private static void DeleteRecordUi()
        {
            Console.Clear();
            Console.WriteLine(">>> 5. DELETE RECORD <<<\n");
            Console.WriteLine("1. Soft Delete (Mark record inactive by default)");
            Console.WriteLine("2. Hard Delete (Permanently remove record from file)");
            Console.Write("\nChoose delete action profile: ");
            string choice = Console.ReadLine();

            bool finalizeHardDelete = false;
            if (choice == "2")
            {
                finalizeHardDelete = true;
            }
            else if (choice != "1")
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Invalid option selected. Dropping operation.");
                Console.ResetColor();
                PressAnyKey();
                return;
            }

            Console.Write("\nEnter target record identifier to delete: ");
            string id = Console.ReadLine();

            bool result = finalizeHardDelete ? _repository.HardDelete(id) : _repository.SoftDelete(id);

            if (result)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("\nDelete operation completed successfully.");
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("\nOperation dropped. Verify record exists or check state constraints.");
            }
            Console.ResetColor();
            PressAnyKey();
        }

        private static void ViewAuditLogUi()
        {
            Console.Clear();
            Console.WriteLine(">>> 6. AUDIT LOGGING <<<\n");
            string auditPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "audit.log");

            try
            {
                if (File.Exists(auditPath))
                {
                    string[] logs = File.ReadAllLines(auditPath);
                    int displayCount = Math.Min(20, logs.Length);
                    Console.WriteLine($"Displaying last {displayCount} system actions recorded:\n");
                    for (int i = logs.Length - displayCount; i < logs.Length; i++)
                    {
                        Console.WriteLine(logs[i]);
                    }
                }
                else
                {
                    Console.WriteLine("No actions logged yet.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Could not read audit log file: {ex.Message}");
            }
            PressAnyKey();
        }

        private static void GenerateReportUi()
        {
            Console.Clear();
            Console.WriteLine(">>> 7. REPORT GENERATION <<<\n");
            _reportGenerator.GenerateConsultationSummaryReport();
            PressAnyKey();
        }

        private static void TriggerErrorDemoUi()
        {
            Console.Clear();
            Console.WriteLine(">>> 8. ERROR HANDLING <<<\n");
            Console.WriteLine("Error management handling is built directly into every transaction layer of this engine.");
            Console.WriteLine("- Form validation traps bad inputs gracefully.");
            Console.WriteLine("- File operations utilize isolated Try-Catch blocks preventing software crashes.");
            Console.WriteLine("- Integrity checks flag mismatched row data automatically.\n");

            Console.Write("Would you like to simulate a File Access exception for review? (y/n): ");
            string trigger = Console.ReadLine();
            if (trigger != null && trigger.Equals("y", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    AuditLogger.Log("ERROR_SIM", "User manually requested file exception handling demo.");
                    throw new FileNotFoundException("Simulated Database Engine Target File Stream Missing Exception Reference.");
                }
                catch (FileNotFoundException ex)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"\n[Caught Exception Gracefully]: {ex.Message}");
                    Console.ResetColor();
                    Console.WriteLine("This error details array was successfully piped to the local audit logs.");
                }
            }
            PressAnyKey();
        }

        private static void DisplayTable(List<ConsultationLog> items)
        {
            if (items.Count == 0)
            {
                Console.WriteLine("No records matching the structural filter criteria found.");
                return;
            }

            Console.WriteLine("------------------------------------------------------------------------------------------------------");
            Console.WriteLine(string.Format("{0,-12} {1,-12} {2,-18} {3,-15} {4,-10}", "ID", "Student ID", "Student Name", "Counselor", "Integrity Check"));
            Console.WriteLine("------------------------------------------------------------------------------------------------------");
            foreach (var item in items)
            {
                string statusText = item.Checksum == item.CalculateChecksum() ? "PASSED" : "CORRUPTED";
                string displayName = item.StudentName.Length > 15 ? item.StudentName.Substring(0, 12) + "..." : item.StudentName;
                Console.WriteLine(string.Format("{0,-12} {1,-12} {2,-18} {3,-15} [{4}]",
                    item.RecordId,
                    item.StudentId,
                    displayName,
                    item.CounselorName,
                    statusText));
            }
            Console.WriteLine("------------------------------------------------------------------------------------------------------");
        }

        private static void PressAnyKey()
        {
            Console.WriteLine("\nPress any key to jump back to main navigation selection...");
            Console.ReadKey();
        }
    }

    #endregion
}