using CrossChat.Integrations.Enums;
using CrossChat.Integrations.Models;
using CrossChat.Integrations.Models.Posting;

namespace CrossChat.Integrations.Interfaces
{
	public interface IPostService
	{
		public Task<List<BlogPost>> GetPendingPostsAsync(int profileId,AccessLevel accessLevel, int count);

		public Task<List<BlogPost>> GetOldPublishedPostsAsync(AccessLevel accessLevel);

		public Task<List<BlogPost>> GetPostsAsync(int profileId,NetworkType filterNet, AccessFilter accessFilter, int page, int pageSize);

		public Task<int> GetTotalCountAsync(NetworkType filterNet, AccessFilter accessFilter);


		public Task<PostCountsDto> GetPostCountsAsync(AccessLevel accessLevel);

		public Task<BlogPost?> GetPostByIdAsync(Guid id);
		public Task AddPostAsync(BlogPost post);
		public Task UpdatePostAsync(BlogPost post);
		public Task DeletePostAsync(Guid id);
	}
}
