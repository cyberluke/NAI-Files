// Copyright (c) Files Community
// Licensed under the MIT License.

using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Windows.Win32;
using Windows.Win32.Security.Credentials;

namespace Files.App.Helpers
{
	internal static class NaiEmailClient
	{
		private static readonly HttpClient client = new(new HttpClientHandler { UseProxy = false, UseCookies = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };
		private static unsafe NaiEmailGrant ReadGrant()
		{
			CREDENTIALW* credential = null;
			fixed (char* target = NaiEmailProtocol.GrantTarget)
			{
				if (!PInvoke.CredRead(target, CRED_TYPE.CRED_TYPE_GENERIC, 0, &credential)) throw new InvalidOperationException("NaiEmailGrantMissing".GetLocalizedResource());
			}
			try
			{
				if (credential is null || credential->CredentialBlob is null || credential->CredentialBlobSize is 0 or > 16384 || credential->CredentialBlobSize % 2 != 0)
					throw new FormatException("Invalid dedicated E-Mail grant");
				var bytes = new ReadOnlySpan<byte>(credential->CredentialBlob, (int)credential->CredentialBlobSize);
				using var document = JsonDocument.Parse(new UnicodeEncoding(false, false, true).GetString(bytes));
				return NaiEmailProtocol.ParseGrant(document.RootElement);
			}
			finally
			{
				if (credential is not null)
				{
					if (credential->CredentialBlob is not null && credential->CredentialBlobSize <= 16384) CryptographicOperations.ZeroMemory(new Span<byte>(credential->CredentialBlob, (int)credential->CredentialBlobSize));
					PInvoke.CredFree(credential);
				}
			}
		}
		public static async Task<NaiEmailOverview> OverviewAsync(CancellationToken token)
		{
			using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
			deadline.CancelAfter(TimeSpan.FromSeconds(15));
			try { return await ReadOverviewAsync(deadline.Token); }
			catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TimeoutException("NaiEmailTimeout".GetLocalizedResource()); }
		}
		private static async Task<NaiEmailOverview> ReadOverviewAsync(CancellationToken token)
		{
			// Only a dedicated, owner-approved same-user grant is read. Never enumerate
			// credentials, borrow another application's key, or open its mailbox DB.
			// Re-read the grant on every request so a newly started E-Mail window
			// (or a port/secret rotation) is picked up without restarting Files.
			var grant = await Task.Run(ReadGrant, token); token.ThrowIfCancellationRequested();
			using var request = new HttpRequestMessage(HttpMethod.Get, grant.Endpoint);
			request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", grant.Secret);
			request.Headers.Add("X-Nai-Client-Id", "nai-files");
			using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
			response.EnsureSuccessStatusCode();
			if (response.Content.Headers.ContentLength > 256 * 1024) throw new InvalidOperationException("Overview budget exceeded");
			await using var stream = await response.Content.ReadAsStreamAsync(token);
			using var buffer = new MemoryStream(); var chunk = new byte[8192]; int read;
			while ((read = await stream.ReadAsync(chunk, token)) > 0)
			{
				if (buffer.Length + read > 256 * 1024) throw new InvalidOperationException("Overview budget exceeded");
				buffer.Write(chunk, 0, read);
			}
			using var document = JsonDocument.Parse(buffer.ToArray());
			return NaiEmailProtocol.ParseOverview(document.RootElement);
		}
	}
}
