// Copyright (c) Files Community
// Licensed under the MIT License.

namespace Files.App.Services.Pulse.MediaLab
{
	public enum MediaKind
	{
		Video,
		Image,
		Audio,
	}

	public enum MediaLabGroup
	{
		Enhance,
		Create,
		Analyze,
	}

	/// <summary>One operation the Media Lab can run against a file.</summary>
	public sealed class MediaOperation
	{
		public required string Id { get; init; }
		public required string Title { get; init; }
		public required string Description { get; init; }
		public required MediaKind Kind { get; init; }
		public required MediaLabGroup Group { get; init; }

		/// <summary>FFmpeg filters that must exist for this operation to be offered.</summary>
		public string[] RequiredFilters { get; init; } = [];

		/// <summary>FFmpeg encoders that must exist for this operation to be offered.</summary>
		public string[] RequiredEncoders { get; init; } = [];

		/// <summary>True for passes that only print a report (`-f null -`).</summary>
		public bool IsAnalysis { get; init; }

		public string Suffix { get; init; } = "lab";
		public string OutputExtension { get; init; } = string.Empty;

		/// <summary>Returns the output path, or the report text for analysis passes.</summary>
		public required Func<MediaOperationContext, IProgress<double>?, CancellationToken, Task<string?>> Execute { get; init; }
	}

	public sealed record MediaOperationContext(string InputPath, string OutputPath, PulseFfmpeg Ffmpeg);
}
