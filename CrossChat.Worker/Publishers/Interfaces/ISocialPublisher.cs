using CrossChat.Data.Entities.Posting;

namespace CrossChat.Worker.Publishers.Interfaces
{
	public interface ISocialPublisher
	{
		Task PublishAsync(NetworkStateEntity state, string caption, List<string> images);
	}
}
