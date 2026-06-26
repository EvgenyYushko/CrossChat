using CrossChat.Integrations.Enums;

namespace CrossChat.Integrations.Models.Posting
{
	public class NetworkPostData
	{
		public string Caption { get; set; } = "";
		public SocialStatus Status { get; set; } = SocialStatus.None;
	}
}
