﻿/*
Utility program to update a single folder or drive in a mysys database. Saves time if your system has millions of files.
On a newly created database, it will create 2 extra indexes which may take some time, but once they're created the next run(s) will be much faster.
This is for more advanced users with at least 5 million files, otherwise might as well just create a new database since it doesn't take very long.

The usage is simple: updatedb "X:\folder name" [-nr]
You can also use a dot to specify current folder, or a folder name relative to it.
Use quotes if the path contains spaces.
New in version 1.1, you can add -nr switch for non-recursive folder scan. It can be placed before or after the folder name.

Might be a good idea to use an elevated cmd prompt to include restricted files, if any.
Now in mysys, click Search to refresh affected tabs. If a drive was added or deleted, however, you'd need to open a new tab for it to be reflected in the Drives drop down list.

If you don't have mysys, give it a try, you won't regret it! You can read more about it and download from https://integritech.ca, it is free to try for a full year! Full featured! Not even a nag nor ads.

For convenience, signed copies of MysysSetup.zip and FoxitReader10.1.0.37527_Setup.zip are included in the Releases section.

Never have trouble finding (or playing, or viewing) your files again, system wide!

Rest assured there is no communication done by mysys to any server whatsoever, well apart from the built-in web browser which can, but it was more intended for viewing local files.

IntegriTech Inc.
https://integritech.ca

*/

using System;
using System.Collections;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace updatedb
{
    class Program
    {
        const long atrillion = 1_000_000_000_000;
        static string AppDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "mysys");
        static string DBPath = Path.Combine(AppDataPath, "mysys.db");
        const string basesql = "select files.id, files.filename || (case length(files.fileext) when 1 then '' else files.fileext end) as filename, folders.folder, " +
            "folders.folderid from myfts2 inner join folders on folders.folderid = files.folderid join files on myfts2.docid = files.id where myfts2 match '{}'";
        const string basesqlnr = "select files.id, files.filename || (case length(files.fileext) when 1 then '' else files.fileext end) as filename, folders.folder, " +
            "folders.folderid from folders inner join files on files.folderid = folders.folderid where folders.folder COLLATE NOCASE = '{}'";
        static Dictionary<ulong, ulong> dblookup = new Dictionary<ulong, ulong>();
        static Dictionary<ulong, string> disklookup = new Dictionary<ulong, string>();
        static SQLiteConnection sqlite_conn = null;
        static ArrayList deletes = new ArrayList(30000);
        static ArrayList inserts = new ArrayList(30000);
        static string glbrootfolder = "";
        static long dbcount = 0;
        static long filecount = 0;
        static long glbstart = -1;
        static Stopwatch indexsw;
        static long delcount = 0;
        static bool glbnr = false;

        static ulong myhash1(string str)
        {
            ulong hash = 5381;
            foreach (char ch in str)
                hash = ((hash << 5) + hash) ^ ch;
            return hash;
        }

        static ulong myhash2(string str)
        {
            const ulong fnvPrime = 1099511628211;
            ulong hash = 14695981039346656037;
            foreach (char ch in str)
            {
                hash ^= ch;
                hash *= fnvPrime;
            }
            return hash;
        }

        static ulong myhash(string str)
        {
            ulong hash1 = myhash1(str);
            ulong hash2 = myhash2(str);
            return hash1 ^ (hash2 << 1);
        }

        static string sanitize(string fileName)
        {
            StringBuilder sanitized = new StringBuilder();
            for (int i = 0; i < fileName.Length; i++)
            {
                char c = fileName[i];
                if (char.IsHighSurrogate(c))
                {
                    if (i + 1 < fileName.Length && char.IsLowSurrogate(fileName[i + 1]))
                    {
                        sanitized.Append(c);
                        sanitized.Append(fileName[++i]);
                    }
                    else
                        sanitized.Append('\uFFFD');
                }
                else if (char.IsLowSurrogate(c))
                    sanitized.Append('\uFFFD');
                else
                    sanitized.Append(c);
            }
            return sanitized.ToString();
        }

        static SQLiteConnection CreateConnection()
        {
            SQLiteConnection sqlite_conn;
            sqlite_conn = new SQLiteConnection(@"Data Source=" + DBPath + "; Version = 3; Compress = True; ");
            try
            {
                sqlite_conn.Open();
            }
            catch (Exception ex)
            {
                sqlite_conn = null;
                Console.WriteLine(ex.ToString());
            }
            return sqlite_conn;
        }

        static string getsql()
        {
            string drive = "#$" + glbrootfolder[0];
            string folder = glbrootfolder.Replace("'", "''");
            string sql;
            if (glbnr)
                sql = basesqlnr.Replace("{}", folder);
            else if (glbrootfolder.Length > 3)
                sql = basesql.Replace("{}", '"' + folder.Replace(":", "").Replace('\\', ' ') + "\" " + drive);
            else
                sql = basesql.Replace("{}", drive);
            return sql;
        }

        static bool maybegetdelcount()
        {
            if (glbrootfolder.Length == 3 && !Directory.Exists(glbrootfolder) && glbstart != -1)
            {
                delcount = dbcount = readlong("SELECT count(id) FROM files" + Environment.NewLine +
                    "JOIN folders ON folders.folderid = files.folderid" + Environment.NewLine +
                    $"WHERE folders.folder LIKE '{glbrootfolder}%'");
                return true;
            }
            return false;
        }

        static void readdb()
        {
            if (maybegetdelcount())
                return;
            string sql = getsql();
            string withslash, lowerroot;
            lowerroot = withslash = glbrootfolder.ToLowerInvariant();
            if (glbrootfolder.Length > 3)
                withslash += '\\';

            SQLiteDataReader sqlite_datareader;
            using (SQLiteCommand sqlite_cmd = sqlite_conn.CreateCommand())
            {
                sqlite_cmd.CommandText = sql;
                sqlite_datareader = sqlite_cmd.ExecuteReader();
                while (sqlite_datareader.Read())
                {
                    try
                    {
                        string folder = sqlite_datareader.GetString(2).ToLowerInvariant();
                        if (glbnr || folder == lowerroot || folder.StartsWith(withslash))
                        {
                            string fname = Path.Combine(folder, sqlite_datareader.GetString(1).ToLowerInvariant());
                            dblookup.Add(myhash(fname), (ulong)sqlite_datareader.GetInt64(0));
                            dbcount++;
                        }
                    }
                    catch { }
                }
            }
        }

        static void maketriggers()
        {
            string sql = "SELECT * FROM sqlite_master WHERE type='trigger'";
            SQLiteDataReader sqlite_datareader;
            using (SQLiteCommand sqlite_cmd = sqlite_conn.CreateCommand())
            {
                sqlite_cmd.CommandText = sql;
                sqlite_datareader = sqlite_cmd.ExecuteReader();
                bool hasrows = sqlite_datareader.HasRows;
                sqlite_datareader.Close();
                if (hasrows)
                    return;

                sql = "CREATE TRIGGER filesdeltrigger AFTER DELETE ON files" + Environment.NewLine +
                    "BEGIN" + Environment.NewLine +
                        "DELETE FROM myfts2 WHERE docid = old.id;" + Environment.NewLine +
                        "DELETE FROM myfts3 WHERE docid = old.id;" + Environment.NewLine +
                    "END;" + Environment.NewLine +
                    "CREATE TRIGGER foldersdeltrigger AFTER DELETE ON folders" + Environment.NewLine +
                    "BEGIN" + Environment.NewLine +
                        "DELETE FROM myfts2" + Environment.NewLine +
                        "WHERE docid IN (SELECT id FROM files WHERE files.folderid = old.folderid);" + Environment.NewLine +
                    "END;" + Environment.NewLine +
                    "CREATE TRIGGER filesinstrigger AFTER INSERT ON files" + Environment.NewLine +
                    "BEGIN" + Environment.NewLine +
                        "INSERT INTO myfts2(docid, drive, filename, fileext)" + Environment.NewLine +
                        "VALUES (" + Environment.NewLine +
                            "new.id," + Environment.NewLine +
                            "'#$' || UPPER(SUBSTR((select folder from folders where folderid = new.folderid), 1, 1))," + Environment.NewLine +
                            "(select folder from folders where folderid = new.folderid) || ' ' || new.filename," + Environment.NewLine +
                            "new.fileext" + Environment.NewLine +
                        ");" + Environment.NewLine +
                        "INSERT INTO myfts3(docid, drive, filename, fileext)" + Environment.NewLine +
                        "VALUES (" + Environment.NewLine +
                            "new.id," + Environment.NewLine +
                            "'#$' || UPPER(SUBSTR((select folder from folders where folderid = new.folderid), 1, 1))," + Environment.NewLine +
                            "new.filename," + Environment.NewLine +
                            "new.fileext" + Environment.NewLine +
                        ");" + Environment.NewLine +
                    "END;";

                sqlite_cmd.CommandText = sql;
                sqlite_cmd.ExecuteNonQuery();
            }
        }

        private static void ReadFileList(string rootFolderPath, ParallelLoopState state1 = null)
        {
            try
            {
                if ((File.GetAttributes(rootFolderPath) & FileAttributes.ReparsePoint) != FileAttributes.ReparsePoint)
                {
                    var files = Directory.GetFiles(rootFolderPath);
                    Parallel.ForEach(files, (string afile, ParallelLoopState state) =>
                    {
                        Thread.CurrentThread.Priority = ThreadPriority.Highest;

                        try
                        {
                            ulong hash = myhash(sanitize(afile).ToLowerInvariant());
                            lock (disklookup)
                            {
                                disklookup.Add(hash, afile);
                                if (++filecount % 10000 == 0)
                                    Console.Write("\r" + filecount.ToString("#,##0"));
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine(ex.ToString());
                        }
                    });

                    if (glbnr)
                        return;

                    var directories = Directory.GetDirectories(rootFolderPath);
                    Parallel.ForEach(directories, ReadFileList);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.ToString());
            }
        }

        static void getinserts()
        {
            ulong fid;
            foreach (KeyValuePair<ulong, string> k in disklookup)
            {
                if (!dblookup.TryGetValue(k.Key, out fid))
                    inserts.Add(k.Value);
            }
            Console.WriteLine("Inserts to do: " + inserts.Count.ToString("#,##0"));
        }

        static void getdeletes()
        {
            if (delcount == 0)
            {
                string fname;
                foreach (KeyValuePair<ulong, ulong> k in dblookup)
                {
                    if (!disklookup.TryGetValue(k.Key, out fname))
                        deletes.Add(k.Value);
                }
                delcount = deletes.Count;
            }
            Console.WriteLine("Deletes to do: " + delcount.ToString("#,##0"));
        }

        static string getins()
        {
            StringBuilder sb = new StringBuilder(30000);
            foreach (ulong fileid in deletes)
                sb.Append(fileid + ",");
            return "(" + sb.ToString().TrimEnd(',') + ")";
        }

        static void dodeletes()
        {
            if (delcount == 0)
                return;
            Stopwatch sw = Stopwatch.StartNew();
            if (deletes.Count == 0)
            {
                using (SQLiteCommand sqlite_cmd = sqlite_conn.CreateCommand())
                {
                    sqlite_cmd.CommandText = "DELETE FROM files" + Environment.NewLine +
                        $"WHERE folderid IN (SELECT folderid FROM folders WHERE folder LIKE '{glbrootfolder}%')";
                    sqlite_cmd.ExecuteNonQuery();
                    sqlite_cmd.CommandText = "DELETE FROM folders" + Environment.NewLine +
                        $"WHERE folder LIKE '{glbrootfolder}%'";
                    sqlite_cmd.ExecuteNonQuery();
                }
            }
            else
                execsql("delete from files where id in " + getins());
            Console.WriteLine("Delete time: " + sw.Elapsed);
        }

        static long readlong(string sql)
        {
            long ret = -1;
            try
            {
                using (SQLiteCommand sqlite_cmd = sqlite_conn.CreateCommand())
                {
                    sqlite_cmd.CommandText = sql;
                    object result = sqlite_cmd.ExecuteScalar();
                    if (result != null && result != DBNull.Value)
                        ret = Convert.ToInt64(result);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.ToString());
            }
            return ret;
        }

        static Dictionary<ulong, ulong> getfolderids()
        {
            Dictionary<ulong, ulong> folderids = new Dictionary<ulong, ulong>();
            string folder = getfoldercriteria();
            string sql = "SELECT folderid, folder FROM folders" + Environment.NewLine +
                $"WHERE folder LIKE {folder}";
            string foldername;
            SQLiteDataReader sqlite_datareader;
            using (SQLiteCommand sqlite_cmd = sqlite_conn.CreateCommand())
            {
                sqlite_cmd.CommandText = sql;
                sqlite_datareader = sqlite_cmd.ExecuteReader();
                while (sqlite_datareader.Read())
                {
                    try
                    {
                        foldername = sqlite_datareader.GetString(1).ToLowerInvariant();
                        folderids.Add(myhash(foldername), (ulong)sqlite_datareader.GetInt64(0));
                    }
                    catch { }
                }
            }
            return folderids;
        }

        static void doinserts()
        {
            if (inserts.Count == 0)
                return;
            long inscount = 0;
            Dictionary<ulong, ulong> folderids;
            Stopwatch sw = Stopwatch.StartNew();
            string sql;
            long id, folderid;
            if (glbstart == -1)
            {
                sql = "select max(folderid) from folders";
                folderid = id = (readlong(sql) / atrillion + 1) * atrillion + 1;
                folderids = new Dictionary<ulong, ulong>();
            }
            else
            {
                folderids = getfolderids();
                long end = glbstart + atrillion;
                sql = "select max(folderid)+1 from folders where folderid between " + glbstart + " and " + end;
                folderid = readlong(sql);
                sql = "select max(id)+1 from files where id between " + glbstart + " and " + end;
                id = readlong(sql);
            }

            using (SQLiteCommand sqlite_cmd = sqlite_conn.CreateCommand())
            {
                foreach (string file in inserts)
                {
                    try
                    {
                        string folder = file.Substring(0, file.LastIndexOf('\\'));
                        if (folder.Length == 2)
                            folder += '\\';
                        ulong hash = myhash(sanitize(folder).ToLowerInvariant());
                        string filename = Path.GetFileNameWithoutExtension(file);
                        string ext = Path.GetExtension(file);
                        if (ext == "")
                            ext = ".";

                        ulong fid;
                        if (!folderids.TryGetValue(hash, out fid))
                        {
                            fid = (ulong)folderid++;
                            folderids.Add(hash, fid);
                            sql = "insert into folders (folderid, folder) values (" + fid + ",'" + folder.Replace("'", "''") + "')";
                            sqlite_cmd.CommandText = sql;
                            sqlite_cmd.ExecuteNonQuery();
                        }

                        sql = "insert into files (id, folderid, filename, fileext) values (" + id++ + "," + fid + ",'" + filename.Replace("'", "''") +
                            "','" + ext.Replace("'", "''") + "')";
                        sqlite_cmd.CommandText = sql;
                        sqlite_cmd.ExecuteNonQuery();
                        if (++inscount % 1000 == 0)
                            Console.Write("\r" + "Insert count: " + inscount.ToString("#,##0"));
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine(ex.ToString());
                    }
                }
            }
            Console.WriteLine("\r" + "Insert count: " + inscount.ToString("#,##0"));
            Console.WriteLine("Insert time: " + sw.Elapsed);
        }

        static bool isletter(char c)
        {
            char upperChar = char.ToUpperInvariant(c);
            return upperChar >= 'A' && upperChar <= 'Z';
        }

        static void execsql(string sql)
        {
            try
            {
                using (SQLiteCommand sqlite_cmd = sqlite_conn.CreateCommand())
                {
                    sqlite_cmd.CommandText = sql;
                    sqlite_cmd.ExecuteNonQuery();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
            }
        }

        static bool chknocase()
        {
            bool res = false;
            try
            {   // need new connection otherwise will lock table and can't do drop.
                using (var readConn = new SQLiteConnection(@"Data Source=" + DBPath + "; Version = 3; Compress = True;"))
                {
                    readConn.Open();
                    using (var cmd = new SQLiteCommand("SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='idx_folders_folder' AND sql LIKE '%COLLATE NOCASE%';", readConn))
                        res = (long)cmd.ExecuteScalar() > 0;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
            }
            return res;
        }


        static void syncoffchkindex()
        {
            indexsw = Stopwatch.StartNew();
            execsql("PRAGMA synchronous=OFF; PRAGMA journal_mode=MEMORY;" + Environment.NewLine +
                "CREATE INDEX IF NOT EXISTS idx_folders_folder ON folders(folder COLLATE NOCASE);" + Environment.NewLine +
                "CREATE INDEX IF NOT EXISTS idx_files_folderid ON files(folderid);");
            if (!chknocase())  // re-create index when previous version of updatedb was used.
                execsql("DROP INDEX idx_folders_folder;" + Environment.NewLine +
                    "CREATE INDEX idx_folders_folder ON folders(folder COLLATE NOCASE);");
            indexsw.Stop();
        }

        static long getstart()
        {
            string drive = "#$" + glbrootfolder[0];
            string sql = $"select docid from myfts3 where myfts3 match '{drive}' limit 1";
            long start = readlong(sql);
            if (start != -1)
                start = start / atrillion * atrillion;
            return start;
        }

        static string getfoldercriteria()
        {
            string folder = glbrootfolder.Replace("'", "''");
            if (glbnr)
                folder = "'" + folder + "'";
            else if (folder.Length == 3)
                folder = "'" + folder + "%'";
            else
                folder = "'" + folder + @"\%' OR folder LIKE '" + folder + "'";
            return folder;
        }

        static void cleanup()
        {
            if (glbstart == -1)
                return;
            Stopwatch sw = Stopwatch.StartNew();
            string folder = getfoldercriteria();
            execsql("DELETE FROM folders" + Environment.NewLine +
                $"WHERE (folder LIKE {folder}) and" + Environment.NewLine +
                "NOT EXISTS (SELECT 1 FROM files WHERE files.folderid = folders.folderid)");
            Console.WriteLine("Cleanup: " + sw.Elapsed);
        }

        // thanks to ChatGPT!
        static string GetActualCase(string path)
        {
            path = Path.GetFullPath(path);
            string[] parts = path.Split(new[] { Path.DirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
            string currentPath = parts[0].ToUpperInvariant() + Path.DirectorySeparatorChar;

            for (int i = 1; i < parts.Length; i++)
            {
                if (!Directory.Exists(currentPath))
                {
                    for (; i < parts.Length; i++)
                        currentPath = Path.Combine(currentPath, parts[i]);
                    break;
                }

                string correctName = GetCorrectFolderName(currentPath, parts[i]);
                currentPath = Path.Combine(currentPath, correctName);
            }
            return currentPath;
        }

        static string GetCorrectFolderName(string parentPath, string folderName)
        {
            string[] directories = Directory.GetDirectories(parentPath);
            foreach (string dir in directories)
                if (string.Equals(Path.GetFileName(dir), folderName, StringComparison.OrdinalIgnoreCase))
                    return Path.GetFileName(dir);
            return folderName;
        }

        static bool setglbs(string[] args)
        {
            if (args.Length == 0 || args.Length > 2)
                return false;

            foreach (string arg in args)
            {
                if (arg.Equals("-nr", StringComparison.OrdinalIgnoreCase))
                    glbnr = true;
                else
                    glbrootfolder = arg.Trim();
            }

            return glbrootfolder != "" && (args.Length == 1 || glbnr);
        }

        static void Main(string[] args)
        {
            Console.WriteLine("IntegriTech updatedb for mysys database. (C) 2025 IntegriTech Inc. - https://integritech.ca");
            try
            {
                if (!File.Exists(DBPath))
                {
                    Console.WriteLine(DBPath + " not found!");
                    return;
                }
                if (!setglbs(args))
                {
                    Console.WriteLine("Usage: updatedb \"X:\\folder name\" [-nr]");
                    Console.WriteLine("For current folder: updatedb .");
                    Console.WriteLine("Names relative to current folder are fine too.");
                    Console.WriteLine("Putting a drive or folder that no longer exists will proceed to delete it from the database.");
                    Console.WriteLine("Use -nr switch for non-recursive folder scan.");
                    return;
                }

                glbrootfolder = GetActualCase(glbrootfolder);
                
                Stopwatch sw = Stopwatch.StartNew();
                sqlite_conn = CreateConnection();
                if (sqlite_conn == null)
                    return;

                Console.WriteLine("Reading " + glbrootfolder);
                
                Thread thr = null;
                if (Directory.Exists(glbrootfolder))
                {
                    thr = new Thread(() => ReadFileList(glbrootfolder));
                    thr.IsBackground = true;
                    thr.Start();
                }

                syncoffchkindex();
                maketriggers();
                glbstart = getstart();
                readdb();
                if (thr != null)
                    thr.Join();

                if (filecount >= 10000)
                    Console.WriteLine("\r" + filecount.ToString("#,##0"));
                Console.WriteLine("Disk file count: " + filecount.ToString("#,##0"));
                Console.WriteLine("DB file count: " + dbcount.ToString("#,##0"));

                thr = new Thread(getinserts);
                thr.IsBackground = true;
                thr.Start();

                getdeletes();
                thr.Join();

                dodeletes();
                doinserts();

                cleanup();
                if (indexsw.ElapsedMilliseconds > 1000)
                    Console.WriteLine("Index creation took: " + indexsw.Elapsed);
                Console.WriteLine("Completed in " + sw.Elapsed);
            }
            catch { }
            finally
            {
                if (sqlite_conn != null)
                    sqlite_conn.Close();
                myreadkey();
            }
        }

        static void myreadkey()
        {
            if (!IsRunningFromConsole())
            {
                Console.WriteLine("Press any key to exit...");
                Console.ReadKey(true);
            }
        }

        static bool IsRunningFromConsole()
        {
            string parentProcessName = GetParentProcessName().ToLowerInvariant();
            return parentProcessName == "cmd" || parentProcessName == "powershell" || parentProcessName == "pwsh" || parentProcessName == "bash" ||
                parentProcessName == "taskeng";
        }

        static string GetParentProcessName()
        {
            int parentPid = GetParentProcessId();
            return parentPid == 0 ? "" : Process.GetProcessById(parentPid).ProcessName;
        }

        static int GetParentProcessId()
        {
            var proc = Process.GetCurrentProcess();
            var handle = CreateToolhelp32Snapshot(SnapshotFlags.Process, 0);
            var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32)) };

            if (handle == IntPtr.Zero)
                return 0;

            if (Process32First(handle, ref entry))
            {
                do
                {
                    if (entry.th32ProcessID == proc.Id)
                    {
                        CloseHandle(handle);
                        return (int)entry.th32ParentProcessID;
                    }
                }
                while (Process32Next(handle, ref entry));
            }

            CloseHandle(handle);
            return 0;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESSENTRY32
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        [Flags]
        private enum SnapshotFlags : uint
        {
            Process = 0x00000002,
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(SnapshotFlags dwFlags, uint th32ProcessID);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
        static extern int GetLongPathName(string shortPath, StringBuilder longPath, int buffer);
    }
}
