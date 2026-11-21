// Copyright (c) Files Community
// Licensed under the MIT License.

namespace Files.App.AI
{
	/// <summary>
	/// The AI use cases Files routes independently. One provider/model binding per capability
	/// keeps the feature forms (image analysis, semantic index, assistant) free of provider
	/// pickers: the choice is made once in Settings and reused everywhere.
	/// </summary>
	public enum AiCapability
	{
		Chat,
		Vision,
		Ocr,
		Caption,
		Embedding,
		Rerank,
		Transcription,
	}

	/// <summary>A provider + model chosen for one capability.</summary>
	public sealed record AiCapabilityBinding(string ProviderId, string Model);
}
