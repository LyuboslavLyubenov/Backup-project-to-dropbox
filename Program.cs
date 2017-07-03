namespace backup_to_dropbox
{
    using System;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Threading.Tasks;

    using Dropbox.Api;
    using Dropbox.Api.Files;

    using Ionic.Zip;
    using Ionic.Zlib;

    class Program
    {
        private const int MaxProjectsBackupsCount = 3;
        private const int BigFileChunkSize = 1024 * 1024;

        static void Main(string[] args)
        {
            var token = File.ReadAllText("token");

#if DEBUG
            var projectPath = Console.ReadLine();
#else
            if (args.Length < 1)
            {
                throw new ArgumentNullException();
            }

            var projectPath = args[0];
#endif
            
            if (!Directory.Exists(projectPath))
            {
                throw new ArgumentException("Not existing directory");
            }

            var archiveDirectory = Directory.GetCurrentDirectory() + "\\temp\\";

            if (!Directory.Exists(archiveDirectory))
            {
                Directory.CreateDirectory(archiveDirectory);
            }

            try
            {
                var projectName = new DirectoryInfo(projectPath).Name;
                var dropboxClient = new DropboxClient(token);
                
                if (HaveMoreThan3BackupsForProject(dropboxClient, projectName))
                {
                    DeleteOldestBackup(dropboxClient, projectName);
                }

                var archivePath = ArchiveFolder(projectPath, archiveDirectory);
                var archiveName = Path.GetFileName(archivePath);
                UploadFileToDropbox(
                        dropboxClient,
                        archivePath,
                        $"/backups/{projectName}/{DateTime.Now:ddMMyyyy}/{archiveName}");
            }
            finally
            {
                ClearTempFiles(archiveDirectory);
            }

            Console.ReadLine();
        }

        static void ClearTempFiles(string archiveDirectory)
        {
            if (Directory.Exists(archiveDirectory))
            {
                Directory.GetFiles(archiveDirectory).ToList().ForEach(File.Delete);
                Directory.Delete(archiveDirectory);
            }
        }

        static void DeleteOldestBackup(DropboxClient client, string projectName)
        {
            var oldestBackupFolderPath = GetOldestBackupFolderPath(client, projectName);
            client.Files.DeleteAsync(oldestBackupFolderPath)
                .Wait();
        }

        static string GetOldestBackupFolderPath(DropboxClient client, string projectName)
        {
            var backupsFolders = client.Files.ListFolderAsync($"/backups/{projectName}/");
            backupsFolders.Wait();
            var oldestBackupFolderName = backupsFolders.Result.Entries.Where(e => e.IsFolder)
                .OrderBy(e => e.Name)
                .First()
                .Name;
            return $"/backups/{projectName}/{oldestBackupFolderName}";
        }

        static bool HaveMoreThan3BackupsForProject(DropboxClient client, string projectName)
        {
            var projects = client.Files.ListFolderAsync("/backups/");
            projects.Wait();

            if (!projects.Result.Entries.Any(e => e.IsFolder && e.Name == projectName))
            {
                return false;
            }

            var backupsCount = client.Files.ListFolderAsync($"/backups/{projectName}/");
            backupsCount.Wait();
            return backupsCount.Result.Entries.Count(e => e.IsFolder) > MaxProjectsBackupsCount;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="folderPath"></param>
        /// <param name="archiveDirectory"></param>
        /// <returns>Archive path</returns>
        static string ArchiveFolder(string folderPath, string archiveDirectory)
        {
            var folderName = new DirectoryInfo(folderPath).Name;
            Console.WriteLine($"Archiving folder {folderName}...");
            
            var archivePath = archiveDirectory + folderName + " backup.zip";

            using (var zip = new ZipFile())
            {
                zip.AlternateEncoding = Encoding.UTF8;
                zip.AlternateEncodingUsage = ZipOption.AsNecessary;
                zip.CompressionLevel = CompressionLevel.Default;
                zip.CompressionMethod = CompressionMethod.Deflate;
                zip.AddDirectory(folderPath);
                
                var outputStream = File.Create(archivePath);
                zip.Save(outputStream);
                outputStream.Close();
            }

            Console.WriteLine($"Successfully archived to {archiveDirectory}");

            return archivePath;
        }

        static void UploadFileToDropbox(DropboxClient dropboxClient, string filePath, string destinationPath)
        {
            var fileSize = GetFileSize(filePath);

            if (fileSize > BigFileChunkSize)
            {
                UploadBigFileToDropbox(dropboxClient, filePath, destinationPath)
                    .Wait();
            }
            else
            {
                UploadSmallFileToDropbox(dropboxClient, filePath, destinationPath)
                    .Wait();
            }
        }

        static long GetFileSize(string filePath)
        {
            var fileInfo = new FileInfo(filePath);
            long fileSize = 0;

            using (var stream = fileInfo.OpenRead())
            {
                fileSize = stream.Length;
            }

            return fileSize;
        }

        static async Task UploadSmallFileToDropbox(DropboxClient client, string filePath, string destinationPath)
        {
            var bytes = File.ReadAllBytes(filePath);

            using (var stream = new MemoryStream(bytes))
            {
                await client.Files.UploadAsync(destinationPath, WriteMode.Overwrite.Instance, body: stream);
                Console.WriteLine("Uploaded successfully to dropbox.");
            }
        }

        static async Task UploadBigFileToDropbox(DropboxClient client, string filePath, string destinationPath)
        {   
            var fileContent = File.ReadAllBytes(filePath);

            using (var stream = new MemoryStream(fileContent))
            {
                int numChunks = (int)Math.Ceiling((double)stream.Length / BigFileChunkSize);

                byte[] buffer = new byte[BigFileChunkSize];
                string sessionId = null;

                for (var idx = 0; idx < numChunks; idx++)
                {
                    Console.WriteLine("Start uploading chunk {0}", idx);
                    var byteRead = stream.Read(buffer, 0, BigFileChunkSize);

                    using (MemoryStream memStream = new MemoryStream(buffer, 0, byteRead))
                    {
                        if (idx == 0)
                        {
                            var result = await client.Files.UploadSessionStartAsync(body: memStream);
                            sessionId = result.SessionId;
                        }
                        else
                        {
                            UploadSessionCursor cursor = new UploadSessionCursor(sessionId, (ulong)(BigFileChunkSize * idx));

                            if (idx == numChunks - 1)
                            {
                                await client.Files.UploadSessionFinishAsync(cursor, new CommitInfo(destinationPath), memStream);

                                Console.WriteLine("Uploaded successfully to dropbox.");
                            }
                            else
                            {
                                await client.Files.UploadSessionAppendV2Async(cursor, body: memStream);
                            }
                        }
                    }
                }
            }
        }
    }
}