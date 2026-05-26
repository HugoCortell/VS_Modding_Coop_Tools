using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace VSMCTFDesigner;

internal sealed class TFDesignGUIPreviewRenderer : IRenderer
{
	private static readonly int WindowColor		= ColorUtil.ColorFromRgba(18, 18, 18, 210);
	private static readonly int TitleBarColor	= ColorUtil.ColorFromRgba(45, 45, 45, 235);
	private static readonly int SlotOuterColor	= ColorUtil.ColorFromRgba(210, 210, 210, 210);
	private static readonly int SlotInnerColor	= ColorUtil.ColorFromRgba(35, 35, 35, 180);
	private static readonly int SlotMidColor	= ColorUtil.ColorFromRgba(95, 95, 95, 200);

	private readonly ICoreClientAPI ClientAPI;
	private readonly GUIDialogTFDesignEditor EditorUI;

	private double WindowX = double.NaN;
	private double WindowY = double.NaN;
	private bool DraggingWindow;
	private double DragWindowOffsetX;
	private double DragWindowOffsetY;

	public double RenderOrder => 0.91;
	public int RenderRange => 9999;

	public TFDesignGUIPreviewRenderer(ICoreClientAPI capi, GUIDialogTFDesignEditor editor)
	{
		this.ClientAPI = capi;
		this.EditorUI = editor;

		capi.Event.RegisterRenderer(this, EnumRenderStage.Ortho, "vsmctfdesigner-gui-preview");
		capi.Event.MouseDown	+= OnMouseDown;
		capi.Event.MouseMove	+= OnMouseMove;
		capi.Event.MouseUp		+= OnMouseUp;
	}

	public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
	{
		if (!IsVisible) return;
		if (!TryGetLayout(out GuiPreviewLayout layout)) return;

		ItemSlot? slot = ClientAPI.World.Player.InventoryManager.ActiveHotbarSlot;
		ModelTransform? transform = EditorUI.CurrentTransform;
		if (slot?.Itemstack is null || transform is null) return;

		DrawWindow(layout);
		DrawSlotFrame(layout, z: 420);

		CollectibleObject collectible = slot.Itemstack.Collectible;
		ModelTransform? oldGuiTransform = collectible.GuiTransform;

		try
		{
			collectible.GuiTransform = BuildPreviewTransform(transform, layout);

			ClientAPI.Render.RenderItemstackToGui(
				slot, layout.CenterX, layout.CenterY, layout.ItemZ,
				(float)layout.ItemSize, ColorUtil.WhiteArgb,
				deltaTime, shading: true, rotate: false, showStackSize: false
			);
		}
		finally { collectible.GuiTransform = oldGuiTransform; }

		DrawSlotFrame(layout, z: 620);
	}

	internal bool IsVisible => EditorUI.IsEditing && EditorUI.CurrentContext == TFDesignContext.Gui;

	internal bool PointInsideWindowChrome(int x, int y) { return IsVisible && TryGetLayout(out GuiPreviewLayout layout) && PointInsideTitleBar(layout, x, y); }

	internal bool TryBuildGizmoState(ModelTransform transform, bool localSpace, out GuiPreviewGizmoState state)
	{
		state = default;
		if (!IsVisible || !TryGetLayout(out GuiPreviewLayout layout)) { return false; }

		ModelTransform previewTransform = BuildPreviewTransform(transform, layout);
		Vec3d screenCenter = TransformGuiPoint(previewTransform, layout, new Vec3d(0.5, 0.5, 0.5));

		Vec3d screenBasisX = Sub(TransformGuiPoint
		(
			BuildPreviewTransform(WithTranslationOffset(transform, 1, 0, 0), layout),
			layout, new Vec3d(0.5, 0.5, 0.5)), screenCenter
		);
		Vec3d screenBasisY = Sub(TransformGuiPoint
		(
			BuildPreviewTransform(WithTranslationOffset(transform, 0, 1, 0), layout),
			layout, new Vec3d(0.5, 0.5, 0.5)), screenCenter)
		;
		Vec3d screenBasisZ = Sub(TransformGuiPoint
		(
			BuildPreviewTransform(WithTranslationOffset(transform, 0, 0, 1), layout),
			layout, new Vec3d(0.5, 0.5, 0.5)), screenCenter
		);

		double screenAxisLength = layout.Size * 0.36;
		Vec3d screenAxisX = new(1, 0, 0);
		Vec3d screenAxisY = new(0, -1, 0);
		Vec3d screenAxisZ = new(0.68, 0.45, 0);

		if (localSpace)
		{
			screenAxisX = GetLocalGuiAxis(previewTransform, layout, screenCenter, new Vec3d(1, 0, 0));
			screenAxisY = GetLocalGuiAxis(previewTransform, layout, screenCenter, new Vec3d(0, 1, 0));
			screenAxisZ = GetLocalGuiAxis(previewTransform, layout, screenCenter, new Vec3d(0, 0, 1));
		}

		screenAxisX = SafeNormalize2D(screenAxisX, new Vec3d(1, 0, 0));
		screenAxisY = SafeNormalize2D(screenAxisY, new Vec3d(0, -1, 0));
		screenAxisZ = SafeNormalize2D(screenAxisZ, new Vec3d(0.68, 0.45, 0));

		state = new GuiPreviewGizmoState
		{
			Center = screenCenter,
			AxisX = screenAxisX, AxisY = screenAxisY, AxisZ = screenAxisZ,
			TranslationBasisX = screenBasisX, TranslationBasisY = screenBasisY, TranslationBasisZ = screenBasisZ,
			AxisLength = screenAxisLength, PixelsPerTransformUnit = Math.Max(0.0001, layout.PixelsPerTransformUnit), WorldUnitsPerPreviewPixel = 1.0
		};

		return true;
	}

	private void OnMouseDown(MouseEvent args)
	{
		if (!IsVisible || args.Handled || args.Button != EnumMouseButton.Left) return;
		if (!TryGetLayout(out GuiPreviewLayout layout) || !PointInsideTitleBar(layout, args.X, args.Y)) return;

		DraggingWindow = true;
		DragWindowOffsetX = args.X - layout.WindowX;
		DragWindowOffsetY = args.Y - layout.WindowY;
		args.Handled = true;
	}

	private void OnMouseMove(MouseEvent args)
	{
		if (!DraggingWindow) return;

		SetWindowPosition(args.X - DragWindowOffsetX, args.Y - DragWindowOffsetY);
		args.Handled = true;
	}

	private void OnMouseUp(MouseEvent args)
	{
		if (!DraggingWindow) return;

		DraggingWindow = false;
		args.Handled = true;
	}

	private bool TryGetLayout(out GuiPreviewLayout layout)
	{
		layout = default;

		ItemSlot? slot = ClientAPI.World.Player.InventoryManager.ActiveHotbarSlot;
		bool isItem = slot?.Itemstack?.Class == EnumItemClass.Item;
		bool isBlock = slot?.Itemstack?.Class == EnumItemClass.Block;

		double slotSize = GuiElement.scaled(310.0);
		double padding = GuiElement.scaled(12.0);
		double titleBarHeight = GuiElement.scaled(28.0);
		double windowWidth = slotSize + padding * 2.0;
		double windowHeight = slotSize + titleBarHeight + padding * 2.0;

		if (double.IsNaN(WindowX) || double.IsNaN(WindowY))
		{
			WindowX = ClientAPI.Render.FrameWidth * 0.62 - windowWidth / 2.0;
			WindowY = ClientAPI.Render.FrameHeight * 0.55 - windowHeight / 2.0;
		}

		ClampWindowToScreen(windowWidth, windowHeight);

		double x = WindowX + padding;
		double y = WindowY + titleBarHeight + padding;
		double previewScale = slotSize / Math.Max(1.0, GuiElement.scaled(GuiElementPassiveItemSlot.unscaledSlotSize));

		layout = new GuiPreviewLayout
		{
			WindowX = WindowX, WindowY = WindowY,
			WindowWidth = windowWidth, WindowHeight = windowHeight,
			TitleBarHeight = titleBarHeight,
			X = x, Y = y,
			Size = slotSize,
			CenterX = x + slotSize / 2.0, CenterY = y + slotSize / 2.0,
			ItemSize = GuiElement.scaled(GuiElementPassiveItemSlot.unscaledItemSize) * previewScale,
			ItemZ = 520, ItemOffsetX = isItem ? -3 : 0, ItemOffsetY = isItem ? -1 : 0,
			UpsideDown = isBlock,
			PreviewScale = previewScale, PixelsPerTransformUnit = GuiElement.scaled(1.0) * previewScale
		};

		return true;
	}

	private void DrawWindow(GuiPreviewLayout layout)
	{
		ClientAPI.Render.RenderRectangle((float)layout.WindowX, (float)layout.WindowY, 405f, (float)layout.WindowWidth, (float)layout.WindowHeight, WindowColor);
		ClientAPI.Render.RenderRectangle((float)layout.WindowX, (float)layout.WindowY, 406f, (float)layout.WindowWidth, (float)layout.TitleBarHeight, TitleBarColor);
	}

	private void DrawSlotFrame(GuiPreviewLayout layout, float z)
	{
		ClientAPI.Render.RenderRectangle((float)layout.X, (float)layout.Y, z, (float)layout.Size, (float)layout.Size, SlotOuterColor);
		ClientAPI.Render.RenderRectangle((float)(layout.X + 3), (float)(layout.Y + 3), z + 1, (float)(layout.Size - 6), (float)(layout.Size - 6), SlotMidColor);
		ClientAPI.Render.RenderRectangle((float)(layout.X + 12), (float)(layout.Y + 12), z + 2, (float)(layout.Size - 24), (float)(layout.Size - 24), SlotInnerColor);
	}

	private void SetWindowPosition(double x, double y)
	{
		WindowX = x;
		WindowY = y;

		double slotSize = GuiElement.scaled(310.0);
		double padding = GuiElement.scaled(12.0);
		double titleBarHeight = GuiElement.scaled(28.0);
		ClampWindowToScreen(slotSize + padding * 2.0, slotSize + titleBarHeight + padding * 2.0);
	}

	private void ClampWindowToScreen(double windowWidth, double windowHeight)
	{
		double margin = GuiElement.scaled(6.0);
		WindowX = Math.Clamp(WindowX, margin, Math.Max(margin, ClientAPI.Render.FrameWidth - windowWidth - margin));
		WindowY = Math.Clamp(WindowY, margin, Math.Max(margin, ClientAPI.Render.FrameHeight - windowHeight - margin));
	}

	private static bool PointInsideTitleBar(GuiPreviewLayout layout, int x, int y)
	{
		return x >= layout.WindowX &&
			   x <= layout.WindowX + layout.WindowWidth &&
			   y >= layout.WindowY &&
			   y <= layout.WindowY + layout.TitleBarHeight;
	}

	private static Vec3d GetLocalGuiAxis(ModelTransform previewTransform, GuiPreviewLayout layout, Vec3d center, Vec3d axis)
	{
		Vec3d endpoint = TransformGuiPoint(previewTransform, layout, Add(new Vec3d(0.5, 0.5, 0.5), Scale(axis, 0.35)));
		return Sub(endpoint, center);
	}

	private static Vec3d TransformGuiPoint(ModelTransform previewTransform, GuiPreviewLayout layout, Vec3d point)
	{
		Matrixf matrix = BuildGuiModelMatrix(previewTransform, layout);
		Vec4f result = matrix.TransformVector(new Vec4f((float)point.X, (float)point.Y, (float)point.Z, 1f));
		return new Vec3d(result.X, result.Y, result.Z);
	}

	private static Matrixf BuildGuiModelMatrix(ModelTransform previewTransform, GuiPreviewLayout layout)
	{
		FastVec3f origin = previewTransform.Origin;
		FastVec3f translation = previewTransform.Translation;
		FastVec3f rotation = previewTransform.Rotation;
		FastVec3f scale = previewTransform.ScaleXYZ;

		Matrixf matrix = new();
		matrix.Identity();
		matrix.Translate((int)layout.CenterX + layout.ItemOffsetX, (int)layout.CenterY + layout.ItemOffsetY, layout.ItemZ);
		matrix.Translate(
			origin.X + (float)GuiElement.scaled(translation.X),
			origin.Y + (float)GuiElement.scaled(translation.Y),
			origin.Z * (float)layout.ItemSize + (float)GuiElement.scaled(translation.Z)
		);
		matrix.Scale((float)layout.ItemSize * scale.X, (float)layout.ItemSize * scale.Y, (float)layout.ItemSize * scale.Z);
		matrix.RotateXDeg(rotation.X + (layout.UpsideDown ? 180f : 0f));
		matrix.RotateYDeg(rotation.Y);
		matrix.RotateZDeg(rotation.Z);
		matrix.Translate(-origin.X, -origin.Y, -origin.Z);
		return matrix;
	}

	private static ModelTransform BuildPreviewTransform(ModelTransform transform, GuiPreviewLayout layout)
	{
		ModelTransform previewTransform = transform.Clone();
		previewTransform.Translation.X *= (float)layout.PreviewScale;
		previewTransform.Translation.Y *= (float)layout.PreviewScale;
		previewTransform.Translation.Z *= (float)layout.PreviewScale;
		return previewTransform;
	}

	private static ModelTransform WithTranslationOffset(ModelTransform transform, float x, float y, float z)
	{
		ModelTransform copy = transform.Clone();
		copy.Translation.X += x;
		copy.Translation.Y += y;
		copy.Translation.Z += z;
		return copy;
	}

	private static Vec3d SafeNormalize2D(Vec3d value, Vec3d fallback)
	{
		value.Z = 0;

		double length = Math.Sqrt(value.X * value.X + value.Y * value.Y);
		if (length < 0.0001) { return fallback; }

		value.X /= length;
		value.Y /= length;
		return value;
	}

	private static Vec3d Add(Vec3d left, Vec3d right)		{ return new Vec3d(left.X + right.X, left.Y + right.Y, left.Z + right.Z); }
	private static Vec3d Sub(Vec3d left, Vec3d right)		{ return new Vec3d(left.X - right.X, left.Y - right.Y, left.Z - right.Z); }
	private static Vec3d Scale(Vec3d value, double scale)	{ return new Vec3d(value.X * scale, value.Y * scale, value.Z * scale); }

	public void Dispose()
	{
		ClientAPI.Event.MouseDown -= OnMouseDown;
		ClientAPI.Event.MouseMove -= OnMouseMove;
		ClientAPI.Event.MouseUp -= OnMouseUp;
		ClientAPI.Event.UnregisterRenderer(this, EnumRenderStage.Ortho);
	}

	private struct GuiPreviewLayout
	{
		public double WindowX, WindowY;
		public double WindowWidth, WindowHeight;
		public double TitleBarHeight;
		public double X, Y;
		public double Size;
		public double CenterX, CenterY;
		public double ItemSize;
		public float ItemZ;
		public int ItemOffsetX, ItemOffsetY;
		public bool UpsideDown;
		public double PreviewScale, PixelsPerTransformUnit;
	}
}

internal struct GuiPreviewGizmoState
{
	public Vec3d Center;
	public Vec3d AxisX;
	public Vec3d AxisY;
	public Vec3d AxisZ;
	public Vec3d TranslationBasisX;
	public Vec3d TranslationBasisY;
	public Vec3d TranslationBasisZ;
	public double AxisLength;
	public double PixelsPerTransformUnit;
	public double WorldUnitsPerPreviewPixel;
}
