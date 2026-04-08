using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;

//using static CrossChat.Constants.AppConstants;
using CrossChat.Integrations.Interfaces;

namespace CrossChat.Integrations.Services
{
	public class BlueSkyService : IBlueSkyService
	{
		private readonly HttpClient _httpClient;

		public BlueSkyService()
		{
			_httpClient = new HttpClient();
		}

		public async Task<(string AccessToken, string RefreshToken, int ExpiresIn)?> RefreshTokenAsync(string refreshToken, string privateKeyJson)
		{
			var tokenUrl = "https://bsky.social/oauth/token"; // Или эндпоинт из базы, если он другой

			// 1. Генерируем DPoP подпись для метода POST (без 'ath', так как мы меняем токены)
			var (dpopProof, _) = CreateDPoPProof("POST", tokenUrl, privateKeyJson);

			var values = new Dictionary<string, string>
			{
				{ "grant_type", "refresh_token" },
				{ "refresh_token", refreshToken },
				{ "client_id", "https://crosschat.ru/bluesky/client-metadata.json" }
			};

			var request = new HttpRequestMessage(HttpMethod.Post, tokenUrl) { Content = new FormUrlEncodedContent(values) };
			request.Headers.Add("DPoP", dpopProof);

			var response = await _httpClient.SendAsync(request);
			if (!response.IsSuccessStatusCode) return null;

			var json = await response.Content.ReadAsStringAsync();
			var data = JsonDocument.Parse(json).RootElement;

			return (
				data.GetProperty("access_token").GetString()!,
				data.GetProperty("refresh_token").GetString()!,
				data.GetProperty("expires_in").GetInt32()
			);
		}

		public (string proof, string privateKeyJson) CreateDPoPProof(string method, string url, string? existingKeyJson = null, string? nonce = null, string? accessToken = null)
		{
			ECDsa ecdsa;

			if (string.IsNullOrEmpty(existingKeyJson))
			{
				// Создаем новый ключ
				ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
			}
			else
			{
				// Восстанавливаем ключ из нашего DTO
				var keyDto = JsonSerializer.Deserialize<BlueSkyKeyDto>(existingKeyJson);

				// ВАЖНО: Координаты X и Y передаются через структуру ECPoint в поле Q
				var params_ = new ECParameters
				{
					Curve = ECCurve.NamedCurves.nistP256,
					D = Microsoft.IdentityModel.Tokens.Base64UrlEncoder.DecodeBytes(keyDto!.D),
					Q = new ECPoint
					{
						X = Microsoft.IdentityModel.Tokens.Base64UrlEncoder.DecodeBytes(keyDto.X),
						Y = Microsoft.IdentityModel.Tokens.Base64UrlEncoder.DecodeBytes(keyDto.Y)
					}
				};
				ecdsa = ECDsa.Create(params_);
			}

			var signingKey = new ECDsaSecurityKey(ecdsa);
			var jwk = JsonWebKeyConverter.ConvertFromSecurityKey(signingKey);

			// Публичная часть для заголовка (только X и Y)
			var publicJwkDict = new Dictionary<string, object> {
				{ "kty", "EC" }, { "crv", "P-256" }, { "x", jwk.X }, { "y", jwk.Y }, { "alg", "ES256" }
			};

			var handler = new JwtSecurityTokenHandler();
			var header = new JwtHeader(new SigningCredentials(signingKey, SecurityAlgorithms.EcdsaSha256));
			header["typ"] = "dpop+jwt";
			header["jwk"] = publicJwkDict;

			var payload = new JwtPayload {
				{ "jti", Guid.NewGuid().ToString("N") },
				{ "htm", method.ToUpper() },
				{ "htu", url },
				{ "iat", EpochTime.GetIntDate(DateTime.UtcNow) }
			};

			if (!string.IsNullOrEmpty(nonce)) payload["nonce"] = nonce;

			if (!string.IsNullOrEmpty(accessToken))
			{
				using var sha256 = SHA256.Create();
				var hashBytes = sha256.ComputeHash(Encoding.ASCII.GetBytes(accessToken));
				// Кодируем хэш в Base64Url (без лишних символов)
				var ath = Base64UrlEncoder.Encode(hashBytes);
				payload["ath"] = ath;
			}

			var token = new JwtSecurityToken(header, payload);
			var proof = handler.WriteToken(token);

			// Экспортируем параметры в наш DTO для сохранения
			var p = ecdsa.ExportParameters(true);
			var exportDto = new BlueSkyKeyDto
			{
				X = Base64UrlEncoder.Encode(p.Q.X),
				Y = Base64UrlEncoder.Encode(p.Q.Y),
				D = Base64UrlEncoder.Encode(p.D)
			};
			var fullKeyJson = JsonSerializer.Serialize(exportDto);

			return (proof, fullKeyJson);
		}
	}

	public class BlueSkyKeyDto
	{
		public string? X { get; set; }
		public string? Y { get; set; }
		public string? D { get; set; }
	}
}

