using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace CrossChat.Hubs
{
	[Authorize]
	public class InboxHub : Hub
	{
		/// <summary>
		/// Менеджер подключается к комнате конкретного профиля
		/// </summary>
		public async Task JoinProfileRoom(int profileId)
		{
			var groupName = $"profile_inbox_{profileId}";
			await Groups.AddToGroupAsync(Context.ConnectionId, groupName);
		}

		public async Task LeaveProfileRoom(int profileId)
		{
			var groupName = $"profile_inbox_{profileId}";
			await Groups.RemoveFromGroupAsync(Context.ConnectionId, groupName);
		}
	}
}