using CrossChat.Integrations.Interfaces.Google;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Google.Apis.Upload;
using File = Google.Apis.Drive.v3.Data.File;

namespace CrossChat.Integrations.Services.Google
{
	public class GoogleDriveUploader : IGoogleDriveUploader
	{
		private readonly DriveService _driveService;

		public GoogleDriveUploader(string serviceAccountJson)
		{
			var credential = GoogleCredential.FromJson(serviceAccountJson)
				.CreateScoped(DriveService.Scope.DriveFile);

			_driveService = new DriveService(new BaseClientService.Initializer
			{
				HttpClientInitializer = credential,
				ApplicationName = "CrossChat Media Storage"
			});
		}

		public async Task<string> UploadStreamAsync(Stream stream, string fileName, string driveFolderId, string? contentType = null)
		{
			var mimeType = !string.IsNullOrEmpty(contentType) ? contentType : GetMimeType(fileName);

			var fileMetadata = new File
			{
				Name = fileName,
				Parents = new[] { driveFolderId }
			};

			if (stream.CanSeek && stream.Position != 0)
			{
				stream.Position = 0;
			}

			var request = _driveService.Files.Create(fileMetadata, stream, mimeType);
			request.Fields = "id";

			var result = await request.UploadAsync(CancellationToken.None);

			if (result.Status != UploadStatus.Completed)
			{
				throw new Exception("Ошибка загрузки в Google Drive: " + result.Exception?.Message);
			}

			return request.ResponseBody.Id;
		}

		public async Task DownloadFileAsync(string fileId, string localFilePath)
		{
			var request = _driveService.Files.Get(fileId);
			using var fileStream = new FileStream(localFilePath, FileMode.Create, FileAccess.Write, FileShare.None);
			await request.DownloadAsync(fileStream);
		}

		public async Task<Stream> GetFileStreamAsync(string fileId)
		{
			var request = _driveService.Files.Get(fileId);
			var memoryStream = new MemoryStream();
			await request.DownloadAsync(memoryStream);
			memoryStream.Position = 0;
			return memoryStream;
		}

		public async Task DeleteFileByIdAsync(string fileId)
		{
			if (string.IsNullOrEmpty(fileId)) return;
			try
			{
				await _driveService.Files.Delete(fileId).ExecuteAsync();
			}
			catch (Exception ex)
			{
				Console.WriteLine($"Ошибка при удалении файла {fileId} из Google Drive: {ex.Message}");
			}
		}

		public async Task<string> UploadFileAsync(string localFilePath, string driveFolderId, bool overwrite = false)
		{
			var fileName = Path.GetFileName(localFilePath);

			if (overwrite)
			{
				await DeleteFileAcync(fileName, driveFolderId);
			}

			using var fileStream = new FileStream(localFilePath, FileMode.Open, FileAccess.Read);
			return await UploadStreamAsync(fileStream, fileName, driveFolderId, GetMimeType(localFilePath));
		}

		public async Task DeleteFileAcync(string fileName, string driveFolderId)
		{
			var existingFile = await GetFileByNameAsync(fileName, driveFolderId);
			if (existingFile != null)
			{
				await _driveService.Files.Delete(existingFile.Id).ExecuteAsync();
			}
		}

		public async Task<File> GetFileByNameAsync(string fileName, string folderId = null)
		{
			var listRequest = _driveService.Files.List();
			var escapedName = fileName.Replace("'", @"\'");
			var query = $"name = '{escapedName}' and trashed = false";

			if (!string.IsNullOrEmpty(folderId))
			{
				query += $" and '{folderId}' in parents";
			}

			listRequest.Q = query;
			listRequest.Fields = "files(id, name, mimeType, size, createdTime)";

			var result = await listRequest.ExecuteAsync();
			return result.Files.FirstOrDefault();
		}

		public async Task<IList<File>> GetAllFilesInFolderAsync(string folderId)
		{
			var listRequest = _driveService.Files.List();
			listRequest.Q = $"'{folderId}' in parents and trashed = false";
			listRequest.Fields = "files(id, name, mimeType, size, createdTime, modifiedTime, webViewLink)";
			listRequest.IncludeItemsFromAllDrives = true;
			listRequest.SupportsAllDrives = true;

			var result = await listRequest.ExecuteAsync();
			return result.Files;
		}

		private string GetMimeType(string fileName)
		{
			var extension = Path.GetExtension(fileName).ToLowerInvariant();
			return extension switch
			{
				// Фото
				".jpg" or ".jpeg" => "image/jpeg",
				".png" => "image/png",
				".webp" => "image/webp",
				".gif" => "image/gif",
				".heic" => "image/heic",

				// Видео
				".mp4" => "video/mp4",
				".mov" => "video/quicktime",
				".avi" => "video/x-msvideo",
				".mkv" => "video/x-matroska",
				".webm" => "video/webm",

				// Бэкапы
				".sql" => "application/sql",
				".gz" => "application/gzip",
				".zip" => "application/zip",

				_ => "application/octet-stream"
			};
		}
	}
}
