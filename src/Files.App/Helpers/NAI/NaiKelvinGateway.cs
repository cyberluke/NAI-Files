// Copyright (c) Files Community
// Licensed under the MIT License.

namespace Files.App.Helpers
{
	/// <summary>
	/// Kelvin AI Workstation fallback engine (SOLID adapter).
	///
	/// The canonical LUSI Engine is preferred for every AI feature; this adapter
	/// exposes the Kelvin pairing as the fallback engine and is referenced by
	/// <see cref="NaiGatewayDiscovery"/>. The Kelvin HTTP wire itself lives in
	/// <c>NaiAiClient</c>; Files features keep talking to one engine seam.
	/// </summary>
	internal static class NaiKelvinGateway
	{
		public static bool IsPaired => NaiAiClient.HasKelvinSecret;
	}
}