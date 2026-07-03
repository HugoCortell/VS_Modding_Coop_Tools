using System;
using Vintagestory.API.Client;

namespace VsStringEditor;

public sealed class PickerListEntry : IDisposable
{
	private LoadedTexture? texture;
	private int textureWidth;
	private int textureHeight;
	private string? textureText;

	public PickerListEntry(string title, string value, string? subtitle = null)
	{
		Title = title ?? "";
		Value = value ?? "";
		Subtitle = subtitle ?? "";
		SearchText = (Title + " " + Value + " " + Subtitle).ToLowerInvariant();
	}

	public string Title { get; }
	public string Value { get; }
	public string Subtitle { get; }
	public string SearchText { get; }

	public LoadedTexture GetTexture(ICoreClientAPI capi, CairoFont font, int width, int height)
	{
		string text = string.IsNullOrWhiteSpace(Subtitle) ? Title : $"{Title}   —   {Subtitle}";
		if (texture != null && textureText == text && textureWidth == width && textureHeight == height) return texture;

		texture?.Dispose();

		textureText = text;
		textureWidth = width;
		textureHeight = height;
		texture = new TextTextureUtil(capi).GenTextTexture
		(
			text,
			font,
			width,
			height,
			background: null,
			orientation: EnumTextOrientation.Left,
			demulAlpha: false
		);

		return texture;
	}

	public void Dispose()
	{
		texture?.Dispose();
		texture = null;
	}
}
