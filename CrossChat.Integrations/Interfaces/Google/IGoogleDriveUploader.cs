using File = Google.Apis.Drive.v3.Data.File;

namespace CrossChat.Integrations.Interfaces.Google
{
	public interface IGoogleDriveUploader
	{
		/// <summary>
		/// Загрузка напрямую из потока (Stream из формы IFormFile) в папку Google Drive
		/// </summary>
		Task<string> UploadStreamAsync(Stream stream, string fileName, string driveFolderId, string? contentType = null);

		/// <summary>
		/// Скачивание файла из Google Drive на локальный диск сервера (для публикации в соцсети)
		/// </summary>
		Task DownloadFileAsync(string fileId, string localFilePath);

		/// <summary>
		/// Получение потока для чтения файла напрямую из Google Drive
		/// </summary>
		Task<Stream> GetFileStreamAsync(string fileId);

		/// <summary>
		/// Удаление файла по его ID
		/// </summary>
		Task DeleteFileByIdAsync(string fileId);

		Task<string> UploadFileAsync(string localFilePath, string driveFolderId, bool overwrite = false);
		Task DeleteFileAcync(string fileName, string driveFolderId);
		Task<File> GetFileByNameAsync(string fileName, string folderId = null);
		Task<IList<File>> GetAllFilesInFolderAsync(string folderId);
	}
}
