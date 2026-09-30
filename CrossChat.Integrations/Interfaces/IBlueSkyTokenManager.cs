using CrossChat.Integrations.Services;

namespace CrossChat.Integrations.Interfaces
{
	public interface IBlueSkyTokenManager
	{
		/// <summary>
		/// Потокобезопасное получение гарантированно свежего токена BlueSky с защитой от гонок и повторного использования RefreshToken.
		/// </summary>
		Task<BlueSkyModel?> GetValidTokenAsync(int botDbId);
	}
}
