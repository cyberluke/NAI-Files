// Copyright (c) Files Community
// Licensed under the MIT License.

using System.Text.Json;

namespace Files.App.Helpers
{
	/// <summary>
	/// Active AI engine selection for all Files AI features (SOLID seam).
	///
	/// The canonical LUSI Engine is the FIRST-CLASS, DEFAULT engine; Kelvin AI
	/// Workstation is the labeled fallback. <see cref="ActiveAsync"/> resolves
	/// LUSI first (shared NAI discovery file + well-known gateway ports) and
	/// only falls back to Kelvin when LUSI is unreachable. Callers depend on
	/// the returned adapter wrapper, never on a hard-coded address — so the
	/// engine can be swapped without changing feature code.
	/// </summary>
	public static class NaiGatewayDiscovery
	{
		/// <summary>The name of the LUSI engine ("LUSI") used for labels.</summary>
		public const string LusiName = "LUSI";
		/// <summary>The name of the Kelvin engine used for labels.</summary>
		public const string KelvinName = "Kelvin";

		private static readonly SemaphoreSlim gate = new(1, 1);
		private static NaiLusiGateway? cachedLusi;
		private static DateTimeOffset lastResolved = DateTimeOffset.MinValue;

		/// <summary>An active inference engine (LUSI preferred, else Kelvin). Null when none is reachable.</summary>
		public sealed record ActiveEngine(NaiLusiGateway? Lusi, bool UsingKelvin)
		{
			public string Name => UsingKelvin || Lusi is null ? KelvinName : LusiName;
			public bool IsLusi => !UsingKelvin && Lusi is not null;
		}

		/// <summary>Resolves the active engine. LUSI-first; a session cache keeps a
		/// freshly-unreachable LUSI from being re-probed on every keystroke.</summary>
		public static async Task<ActiveEngine> ActiveAsync(CancellationToken token)
		{
			// Fast path: already resolved LUSI this session.
			if (cachedLusi is not null && DateTimeOffset.UtcNow - lastResolved < TimeSpan.FromSeconds(60))
				return new ActiveEngine(cachedLusi, UsingKelvin: false);

			await gate.WaitAsync(token);
			try
			{
				lastResolved = DateTimeOffset.UtcNow;
				if (cachedLusi is not null) return new ActiveEngine(cachedLusi, UsingKelvin: false);

				// 1) Canonical LUSI engine first.
				var lusi = await NaiLusiGateway.ResolveAsync(token);
				if (lusi is not null)
				{
					cachedLusi = lusi;
					return new ActiveEngine(lusi, UsingKelvin: false);
				}

				// 2) Kelvin fallback (only when LUSI is unreachable).
				return new ActiveEngine(null, UsingKelvin: NaiKelvinGateway.IsPaired);
			}
			finally { gate.Release(); }
		}

		/// <summary>Forgets the cached LUSI resolution (used after pairing/disconnect).</summary>
		public static void Invalidate()
		{
			lastResolved = DateTimeOffset.MinValue;
			cachedLusi = null;
		}
	}
}