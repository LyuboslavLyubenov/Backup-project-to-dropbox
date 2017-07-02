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
        const int BigFileChunkSize = 128 * 1024;

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
                var projectDirectoryName = new DirectoryInfo(projectPath).Name;
                var archivePath = ArchiveFolder(projectPath, archiveDirectory);
                var archiveName = Path.GetFileName(archivePath);
                UploadFileToDropbox(
                        token,
                        archivePath,
                        $"/backups/{projectDirectoryName}/{DateTime.Now:MMddyyyy}/{archiveName}")
                    .Wait();
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

        static async Task UploadFileToDropbox(string token, string filePath, string destinationPath)
        {
            var dropboxClient = new DropboxClient(token);
            var fileSize = GetFileSize(filePath);

            if (fileSize > BigFileChunkSize)
            {
                await UploadBigFileToDropbox(dropboxClient, filePath, destinationPath);
            }
            else
            {
                await UploadSmallFileToDropbox(dropboxClient, filePath, destinationPath);
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
            var fileName = Path.GetFileName(filePath);
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
                                var fileName = Path.GetFileName(filePath);
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