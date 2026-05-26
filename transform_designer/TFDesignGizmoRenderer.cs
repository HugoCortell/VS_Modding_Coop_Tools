using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace VSMCTFDesigner;

internal sealed class TFDesignGizmoRenderer : IRenderer
{
	private const double AxisLength			= 0.72;
	private const double ArrowHeadLength	= 0.14;
	private const double ArrowHeadWidth		= 0.07;
	private const double CubeHalfSize		= 0.055;
	private const double PickDistance		= 12.0;
	private const float GizmoRenderDepth	= 720f;

	private static readonly int Red		= ColorUtil.ColorFromRgba(255, 35, 35, 255);
	private static readonly int Green	= ColorUtil.ColorFromRgba(35, 220, 35, 255);
	private static readonly int Blue	= ColorUtil.ColorFromRgba(45, 120, 255, 255);
	private static readonly int Yellow	= ColorUtil.ColorFromRgba(255, 230, 40, 255);

	private readonly ICoreClientAPI ClientAPI;
	private readonly VSMCTFDesignerModSystem VSMCTFDModSystem;
	private readonly GUIDialogTFDesignEditor EditorUI;
	private readonly TFDesignGUIPreviewRenderer GUIPreviewRenderer;

	private TFDesignAxis HoveredAxis = TFDesignAxis.None;
	private TFDesignAxis DraggedAxis = TFDesignAxis.None;

	private int DragStartX, DragStartY;
	private Vec2d DragScreenAxis = new(1, 0);

	private FastVec3f DragStartTranslation, DragStartRotation, DragStartScale;

	private GizmoState DragStartState;
	private Vec3d DragWorldAxis = new(1, 0, 0);
	private Vec3d DragRotateStartVector = new(1, 0, 0);
	private double DragStartAxisCoordinate;
	private bool DragHasRayAxisCoordinate;
	private bool DragHasRotateVector;

	public double RenderOrder => 0.93;
	public int RenderRange => 9999;

	public TFDesignGizmoRenderer(ICoreClientAPI capi, VSMCTFDesignerModSystem modSystem, GUIDialogTFDesignEditor editor, TFDesignGUIPreviewRenderer guiPreviewRenderer)
	{
		this.ClientAPI = capi;
		this.VSMCTFDModSystem = modSystem;
		this.EditorUI = editor;
		this.GUIPreviewRenderer = guiPreviewRenderer;

		capi.Event.RegisterRenderer(this, EnumRenderStage.Opaque, "vsmctfdesigner-gizmos");
		capi.Event.RegisterRenderer(this, EnumRenderStage.Ortho, "vsmctfdesigner-gizmos-ortho");
		capi.Event.MouseDown	+= OnMouseDown;
		capi.Event.MouseMove	+= OnMouseMove;
		capi.Event.MouseUp		+= OnMouseUp;
	}

	public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
	{
		if (!ShouldDraw || !TryBuildState(EditorUI.CurrentTransform, out GizmoState state)) return;
		if (state.Ortho != (stage == EnumRenderStage.Ortho)) return;

		if (DraggedAxis == TFDesignAxis.None) { HoveredAxis = PickAxis(state, ClientAPI.Input.MouseX, ClientAPI.Input.MouseY); }
		if (!state.Ortho) ClientAPI.Render.GLDisableDepthTest();
		DrawActiveGizmo(state);
		if (!state.Ortho) ClientAPI.Render.GLEnableDepthTest();
	}

	private void DrawActiveGizmo(GizmoState state)
	{
		switch (VSMCTFDModSystem.GizmoMode)
		{
			case TFDesignGizmoMode.Move:
				DrawMoveGizmo(state);
			break;

			case TFDesignGizmoMode.Scale:
				DrawScaleGizmo(state);
			break;

			case TFDesignGizmoMode.Rotate:
				DrawRotateGizmo(state);
			break;
		}
	}

	private bool ShouldDraw => EditorUI.IsEditing && VSMCTFDModSystem.GizmoMode != TFDesignGizmoMode.None;

	private void OnMouseDown(MouseEvent args)
	{
		if (args.Handled || !ShouldDraw || args.Button != EnumMouseButton.Left) return;
		if (VSMCTFDModSystem.PointInsideTfDesignUi(args.X, args.Y)) return;
		if (!TryBuildState(EditorUI.CurrentTransform, out GizmoState state)) return;

		TFDesignAxis picked = PickAxis(state, args.X, args.Y);
		if (picked == TFDesignAxis.None) return;

		DraggedAxis = picked;
		HoveredAxis = picked;
		DragStartState = state;
		DragStartX = args.X;
		DragStartY = args.Y;
		DragScreenAxis = GetDragScreenAxis(state, picked, args.X, args.Y);
		DragWorldAxis = GetWorldAxis(state, picked);
		if (state.Ortho)
		{
			DragStartAxisCoordinate = GetOrthoAxisCoordinate(state.Center, DragWorldAxis, args.X, args.Y);
			DragHasRayAxisCoordinate = true;
			DragHasRotateVector = TryGetOrthoRotateVector(state.Center, args.X, args.Y, out DragRotateStartVector);
		}
		else
		{
			DragHasRayAxisCoordinate = TryGetAxisCoordinate(state.Center, DragWorldAxis, args.X, args.Y, out DragStartAxisCoordinate);
			DragHasRotateVector = TryGetRotateVector(state.Center, DragWorldAxis, args.X, args.Y, out DragRotateStartVector);
		}

		ModelTransform? transform = EditorUI.CurrentTransform;
		if (transform is not null)
		{
			DragStartTranslation = transform.Translation;
			DragStartRotation = transform.Rotation;
			DragStartScale = transform.ScaleXYZ;
		}

		args.Handled = true;
	}

	private void OnMouseMove(MouseEvent args)
	{
		if (!ShouldDraw) { HoveredAxis = TFDesignAxis.None; return; }

		if (DraggedAxis == TFDesignAxis.None)
		{
			if (args.Handled) { HoveredAxis = TFDesignAxis.None; return; }
			if (!TryBuildState(EditorUI.CurrentTransform, out GizmoState state)) { HoveredAxis = TFDesignAxis.None; return; }

			HoveredAxis = VSMCTFDModSystem.PointInsideTfDesignUi(args.X, args.Y)
				? TFDesignAxis.None
				: PickAxis(state, args.X, args.Y);
			return;
		}

		ApplyDrag(args.X, args.Y);
		args.Handled = true;
	}

	private void OnMouseUp(MouseEvent args)
	{
		if (DraggedAxis == TFDesignAxis.None) return;

		DraggedAxis = TFDesignAxis.None;
		HoveredAxis = TFDesignAxis.None;
		args.Handled = true;
	}

	private void ApplyDrag(int mouseX, int mouseY)
	{
		double dx = mouseX - DragStartX;
		double dy = mouseY - DragStartY;
		double fallbackPixels = dx * DragScreenAxis.X + dy * DragScreenAxis.Y;

		switch (VSMCTFDModSystem.GizmoMode)
		{
			case TFDesignGizmoMode.Move:
				ApplyMoveDrag(GetAxisDelta(mouseX, mouseY, fallbackPixels));
			break;

			case TFDesignGizmoMode.Scale:
				ApplyScaleDrag(GetAxisDelta(mouseX, mouseY, fallbackPixels));
			break;

			case TFDesignGizmoMode.Rotate:
				ApplyRotateDrag(mouseX, mouseY, fallbackPixels);
			break;
		}
	}

	private double GetAxisDelta(int mouseX, int mouseY, double fallbackPixels)
	{
		if (DragStartState.Ortho) { return GetOrthoAxisCoordinate(DragStartState.Center, DragWorldAxis, mouseX, mouseY) - DragStartAxisCoordinate; }
		if (DragHasRayAxisCoordinate && TryGetAxisCoordinate(DragStartState.Center, DragWorldAxis, mouseX, mouseY, out double coordinate))
		{ return coordinate - DragStartAxisCoordinate; }

		return fallbackPixels * 0.01;
	}

	private bool TryGetDragRotateVector(int mouseX, int mouseY, out Vec3d vector)
	{
		return DragStartState.Ortho
			? TryGetOrthoRotateVector(DragStartState.Center, mouseX, mouseY, out vector)
			: TryGetRotateVector(DragStartState.Center, DragWorldAxis, mouseX, mouseY, out vector);
	}

	private static double GetOrthoAxisCoordinate(Vec3d axisCenter, Vec3d axisDirection, int mouseX, int mouseY)
	{
		Vec3d dir = SafeNormalize2D(new Vec3d(axisDirection.X, axisDirection.Y, 0), new Vec3d(1, 0, 0));
		return (mouseX - axisCenter.X) * dir.X + (mouseY - axisCenter.Y) * dir.Y;
	}

	private static bool TryGetOrthoRotateVector(Vec3d center, int mouseX, int mouseY, out Vec3d vector)
	{
		vector = new Vec3d(mouseX - center.X, mouseY - center.Y, 0);
		if (vector.LengthSq() < 4.0) { return false; }

		vector.Normalize();
		return true;
	}

	private void ApplyMoveDrag(double axisWorldDelta)
	{
		Vec3d translationDelta;

		if (DragStartState.GuiPreview && DraggedAxis == TFDesignAxis.Z)
		{
			translationDelta = new Vec3d(0, 0, axisWorldDelta / DragStartState.OrthoPixelsPerTransformUnit);
		}
		else
		{
			Vec3d desiredWorldDelta = Scale(DragWorldAxis, axisWorldDelta);
			translationDelta = DragStartState.TranslationBasis.WorldToTransformDelta(desiredWorldDelta);
		}

		if (EditorUI.IncludeGizmoInIncrement)
		{
			double interval = Math.Max(0.0001, EditorUI.TransformIncrement);
			translationDelta.X = Snap(translationDelta.X, interval);
			translationDelta.Y = Snap(translationDelta.Y, interval);
			translationDelta.Z = Snap(translationDelta.Z, interval);
		}

		EditorUI.SetGizmoTranslation(
			DragStartTranslation.X + (float)translationDelta.X,
			DragStartTranslation.Y + (float)translationDelta.Y,
			DragStartTranslation.Z + (float)translationDelta.Z
		);
	}

	private void ApplyScaleDrag(double axisWorldDelta)
	{
		int deltaPercent = (int)Math.Round(DragStartState.GuiPreview ? axisWorldDelta / DragStartState.WorldUnitsPerPreviewPixel * 0.5 : axisWorldDelta * 100.0);

		float x = DragStartScale.X;
		float y = DragStartScale.Y;
		float z = DragStartScale.Z;

		switch (DraggedAxis)
		{
			case TFDesignAxis.X:
				x = ScaleAxis(DragStartScale.X, deltaPercent);
			break;

			case TFDesignAxis.Y:
				y = ScaleAxis(DragStartScale.Y, deltaPercent);
			break;

			case TFDesignAxis.Z:
				z = ScaleAxis(DragStartScale.Z, deltaPercent);
			break;
		}

		EditorUI.SetGizmoScale(x, y, z);
	}

	private void ApplyRotateDrag(int mouseX, int mouseY, double fallbackPixels)
	{
		double deltaDegrees;

		if (DragStartState.Ortho)
		{
			deltaDegrees = fallbackPixels * 0.5;
		}
		else if (DragHasRotateVector && TryGetDragRotateVector(mouseX, mouseY, out Vec3d currentVector))
		{
			double dot = Math.Clamp(Dot(DragRotateStartVector, currentVector), -1.0, 1.0);
			double signed = Dot(DragWorldAxis, DragRotateStartVector.Cross(currentVector));
			deltaDegrees = Math.Atan2(signed, dot) * GameMath.RAD2DEG;
		}
		else { deltaDegrees = fallbackPixels * 0.5; }

		int snappedDegrees = (int)Math.Round(deltaDegrees);
		Matrix3 startActualRotation = Matrix3.FromEulerDegrees(
			DragStartRotation.X + DragStartState.AttachmentRotation.X,
			DragStartRotation.Y + DragStartState.AttachmentRotation.Y,
			DragStartRotation.Z + DragStartState.AttachmentRotation.Z
		);

		Matrix3 newActualRotation;

		if (VSMCTFDModSystem.LocalSpace)
		{
			Matrix3 axisRotation = Matrix3.FromAxisAngle(GetCanonicalAxis(DraggedAxis), snappedDegrees * GameMath.DEG2RAD);
			newActualRotation = startActualRotation.Mul(axisRotation);
		}
		else
		{
			Vec3d rotationAxis = DragStartState.GuiPreview ? GetCanonicalAxis(DraggedAxis) : DragWorldAxis;
			Matrix3 axisRotation = Matrix3.FromAxisAngle(rotationAxis, snappedDegrees * GameMath.DEG2RAD);
			Matrix3 startWorldRotation = DragStartState.RotationParentBasis.Mul(startActualRotation);
			Matrix3 newWorldRotation = axisRotation.Mul(startWorldRotation);
			newActualRotation = DragStartState.RotationParentBasisInverse.Mul(newWorldRotation);
		}

		Vec3d euler = newActualRotation.Orthonormalized().ToEulerDegrees();

		EditorUI.SetGizmoRotation(
			NormalizeDegrees((float)(euler.X - DragStartState.AttachmentRotation.X)),
			NormalizeDegrees((float)(euler.Y - DragStartState.AttachmentRotation.Y)),
			NormalizeDegrees((float)(euler.Z - DragStartState.AttachmentRotation.Z))
		);
	}

	private static float ScaleAxis(float startScale, int deltaPercent)
	{
		float sign = startScale < 0 ? -1f : 1f;
		int startPercent = Math.Clamp((int)Math.Round(Math.Abs(startScale) * 100f), 1, 300);
		int newPercent = Math.Clamp(startPercent + deltaPercent, 1, 300);
		return sign * newPercent / 100f;
	}

	private static float NormalizeDegrees(float degrees)
	{
		while (degrees > 180f) degrees -= 360f;
		while (degrees < -180f) degrees += 360f;
		return degrees;
	}

	private static double Snap(double value, double interval) { return Math.Round(value / interval) * interval; }

	private static double GetAxisLength(GizmoState state) { return state.AxisLengthOverride > 0 ? state.AxisLengthOverride : AxisLength; }

	private void DrawMoveGizmo(GizmoState state)
	{
		DrawAxisArrow(state, TFDesignAxis.X);
		DrawAxisArrow(state, TFDesignAxis.Y);
		DrawAxisArrow(state, TFDesignAxis.Z);
	}

	private void DrawScaleGizmo(GizmoState state)
	{
		DrawAxisCube(state, TFDesignAxis.X);
		DrawAxisCube(state, TFDesignAxis.Y);
		DrawAxisCube(state, TFDesignAxis.Z);
	}

	private void DrawRotateGizmo(GizmoState state)
	{
		DrawAxisCircle(state, TFDesignAxis.X);
		DrawAxisCircle(state, TFDesignAxis.Y);
		DrawAxisCircle(state, TFDesignAxis.Z);
	}

	private void DrawAxisArrow(GizmoState state, TFDesignAxis axis)
	{
		Vec3d direction = GetWorldAxis(state, axis);
		Vec3d end = Add(state.Center, Scale(direction, GetAxisLength(state)));
		int color = AxisColor(axis);

		DrawLine(state, state.Center, end, color);

		double arrowHeadLength = state.Ortho ? 18.0 : ArrowHeadLength;
		double arrowHeadWidth = state.Ortho ? 9.0 : ArrowHeadWidth;
		Vec3d side = Perpendicular(direction, state.CameraUp);
		Vec3d side2 = Perpendicular(direction, state.CameraRight);
		Vec3d basePoint = Add(end, Scale(direction, -arrowHeadLength));

		DrawLine(state, end, Add(basePoint, Scale(side, arrowHeadWidth)), color);
		DrawLine(state, end, Add(basePoint, Scale(side, -arrowHeadWidth)), color);
		DrawLine(state, end, Add(basePoint, Scale(side2, arrowHeadWidth)), color);
		DrawLine(state, end, Add(basePoint, Scale(side2, -arrowHeadWidth)), color);
	}

	private void DrawAxisCube(GizmoState state, TFDesignAxis axis)
	{
		Vec3d direction = GetWorldAxis(state, axis);
		Vec3d end = Add(state.Center, Scale(direction, GetAxisLength(state)));
		int color = AxisColor(axis);

		DrawLine(state, state.Center, end, color);
		DrawWireCube(state, end, state.Ortho ? CubeHalfSize * 120.0 : CubeHalfSize, color);
	}

	private void DrawAxisCircle(GizmoState state, TFDesignAxis axis)
	{
		GetCircleBasis(state, axis, out Vec3d u, out Vec3d v);

		int color = AxisColor(axis);
		Vec3d previous = Add(state.Center, Scale(u, GetAxisLength(state)));
		const int segments = 64;

		for (int i = 1; i <= segments; i++)
		{
			double angle = GameMath.TWOPI * i / segments;
			Vec3d point = Add(state.Center, Add(Scale(u, Math.Cos(angle) * GetAxisLength(state)), Scale(v, Math.Sin(angle) * GetAxisLength(state))));
			DrawLine(state, previous, point, color);
			previous = point;
		}
	}

	private void GetCircleBasis(GizmoState state, TFDesignAxis axis, out Vec3d u, out Vec3d v)
	{
		if (state.Ortho)
		{
			// GUI mode is still drawn in screen space, we try to fake projected 3D rings to avoid flattening the gizmo.
			Vec3d x = GetWorldAxis(state, TFDesignAxis.X);
			Vec3d y = GetWorldAxis(state, TFDesignAxis.Y);
			Vec3d z = Scale(GetWorldAxis(state, TFDesignAxis.Z), 0.55);

			switch (axis)
			{
				case TFDesignAxis.X:
					u = y;
					v = z;
				return;

				case TFDesignAxis.Y:
					u = z;
					v = x;
				return;

				case TFDesignAxis.Z:
				default:
					u = x;
					v = y;
				return;
			}
		}

		Vec3d normal = GetWorldAxis(state, axis);
		u = Perpendicular(normal, state.CameraUp);
		v = normal.Cross(u).Normalize();
	}

	private int AxisColor(TFDesignAxis axis)
	{
		if (axis == DraggedAxis || axis == HoveredAxis) { return Yellow; }

		return axis switch
		{
			TFDesignAxis.X => Red,
			TFDesignAxis.Y => Green,
			TFDesignAxis.Z => Blue,
			_ => Yellow
		};
	}

	private void DrawWireCube(GizmoState state, Vec3d center, double halfSize, int color)
	{
		Vec3d x = Scale(new Vec3d(1, 0, 0), halfSize);
		Vec3d y = Scale(new Vec3d(0, 1, 0), halfSize);
		Vec3d z = Scale(new Vec3d(0, 0, 1), halfSize);

		Vec3d[] points =
		[
			Add(center, Add(Add(Scale(x, -1), Scale(y, -1)), Scale(z, -1))),
			Add(center, Add(Add(Scale(x,  1), Scale(y, -1)), Scale(z, -1))),
			Add(center, Add(Add(Scale(x,  1), Scale(y,  1)), Scale(z, -1))),
			Add(center, Add(Add(Scale(x, -1), Scale(y,  1)), Scale(z, -1))),
			Add(center, Add(Add(Scale(x, -1), Scale(y, -1)), Scale(z,  1))),
			Add(center, Add(Add(Scale(x,  1), Scale(y, -1)), Scale(z,  1))),
			Add(center, Add(Add(Scale(x,  1), Scale(y,  1)), Scale(z,  1))),
			Add(center, Add(Add(Scale(x, -1), Scale(y,  1)), Scale(z,  1)))
		];

		DrawLine(state, points[0], points[1], color);
		DrawLine(state, points[1], points[2], color);
		DrawLine(state, points[2], points[3], color);
		DrawLine(state, points[3], points[0], color);

		DrawLine(state, points[4], points[5], color);
		DrawLine(state, points[5], points[6], color);
		DrawLine(state, points[6], points[7], color);
		DrawLine(state, points[7], points[4], color);

		DrawLine(state, points[0], points[4], color);
		DrawLine(state, points[1], points[5], color);
		DrawLine(state, points[2], points[6], color);
		DrawLine(state, points[3], points[7], color);
	}

	private void DrawLine(GizmoState state, Vec3d start, Vec3d end, int color)
	{
		if (state.Ortho) { DrawOrthoLine(start, end, color); return; }
		DrawLine(start, end, color);
	}

	private void DrawOrthoLine(Vec3d start, Vec3d end, int color)
	{
		double dx = end.X - start.X;
		double dy = end.Y - start.Y;
		double length = Math.Sqrt(dx * dx + dy * dy);

		if (length < 0.01) { return; }

		double thickness = Math.Max(2.0, GuiElement.scaled(2.0));
		int steps = Math.Max(1, (int)Math.Ceiling(length / Math.Max(2.0, thickness)));

		for (int i = 0; i <= steps; i++)
		{
			double t = (double)i / steps;
			double x = start.X + dx * t;
			double y = start.Y + dy * t;

			ClientAPI.Render.RenderRectangle(
				(float)(x - thickness / 2.0),
				(float)(y - thickness / 2.0),
				GizmoRenderDepth,
				(float)thickness,
				(float)thickness,
				ToGuiColor(color)
			);
		}
	}

	private static int ToGuiColor(int color)
	{
		// Due to ColorUtil not being consistent with its mappings, we do it ourselves to avoid surprises.
		int r = color & 0xff;
		int g = (color >> 8) & 0xff;
		int b = (color >> 16) & 0xff;
		int a = (color >> 24) & 0xff;

		return ColorUtil.ToRgba(a, r, g, b);
	}

	private void DrawLine(Vec3d start, Vec3d end, int color)
	{
		BlockPos origin = new((int)Math.Floor(start.X), (int)Math.Floor(start.Y), (int)Math.Floor(start.Z));

		ClientAPI.Render.RenderLine(
			origin,
			(float)(start.X - origin.X),
			(float)(start.Y - origin.Y),
			(float)(start.Z - origin.Z),
			(float)(end.X - origin.X),
			(float)(end.Y - origin.Y),
			(float)(end.Z - origin.Z),
			color
		);
	}

	private TFDesignAxis PickAxis(GizmoState state, int mouseX, int mouseY)
	{
		return VSMCTFDModSystem.GizmoMode switch
		{
			TFDesignGizmoMode.Rotate => PickCircleAxis(state, mouseX, mouseY),
			TFDesignGizmoMode.Move or TFDesignGizmoMode.Scale => PickLinearAxis(state, mouseX, mouseY),
			_ => TFDesignAxis.None
		};
	}

	private TFDesignAxis PickLinearAxis(GizmoState state, int mouseX, int mouseY)
	{
		double best = PickDistance;
		TFDesignAxis picked = TFDesignAxis.None;

		foreach (TFDesignAxis axis in new[] { TFDesignAxis.X, TFDesignAxis.Y, TFDesignAxis.Z })
		{
			Vec3d end = Add(state.Center, Scale(GetWorldAxis(state, axis), GetAxisLength(state)));

			Vec2d a;
			Vec2d b;

			if (state.Ortho)
			{
				a = new Vec2d(state.Center.X, state.Center.Y);
				b = new Vec2d(end.X, end.Y);
			}
			else if (!Project(state.Center, out a) || !Project(end, out b)) { continue; }

			double distance = DistancePointToSegment(mouseX, mouseY, a, b);
			if (distance >= best) continue;

			best = distance;
			picked = axis;
		}

		return picked;
	}

	private TFDesignAxis PickCircleAxis(GizmoState state, int mouseX, int mouseY)
	{
		double best = PickDistance;
		TFDesignAxis picked = TFDesignAxis.None;

		foreach (TFDesignAxis axis in new[] { TFDesignAxis.X, TFDesignAxis.Y, TFDesignAxis.Z })
		{
			GetCircleBasis(state, axis, out Vec3d u, out Vec3d v);

			Vec3d first = Add(state.Center, Scale(u, GetAxisLength(state)));
			if (!GetScreenPoint(state, first, out Vec2d previous)) continue;

			const int segments = 64;
			for (int i = 1; i <= segments; i++)
			{
				double angle = GameMath.TWOPI * i / segments;
				Vec3d point = Add(state.Center, Add(Scale(u, Math.Cos(angle) * GetAxisLength(state)), Scale(v, Math.Sin(angle) * GetAxisLength(state))));
				if (!GetScreenPoint(state, point, out Vec2d projected)) continue;

				double distance = DistancePointToSegment(mouseX, mouseY, previous, projected);
				if (distance < best)
				{
					best = distance;
					picked = axis;
				}

				previous = projected;
			}
		}

		return picked;
	}

	private Vec2d GetDragScreenAxis(GizmoState state, TFDesignAxis axis, int mouseX, int mouseY)
	{
		if (state.Ortho && VSMCTFDModSystem.GizmoMode == TFDesignGizmoMode.Rotate && TryGetCircleDragTangent(state, axis, mouseX, mouseY, out Vec2d tangent))
		{
			return tangent;
		}

		return GetScreenAxis(state, axis);
	}

	private bool TryGetCircleDragTangent(GizmoState state, TFDesignAxis axis, int mouseX, int mouseY, out Vec2d tangent)
	{
		tangent = new Vec2d(1, 0);

		GetCircleBasis(state, axis, out Vec3d u, out Vec3d v);

		Vec3d previousPoint = Add(state.Center, Scale(u, GetAxisLength(state)));
		if (!GetScreenPoint(state, previousPoint, out Vec2d previous)) return false;

		const int segments = 96;
		double best = double.MaxValue;
		Vec2d bestTangent = tangent;

		for (int i = 1; i <= segments; i++)
		{
			double angle = GameMath.TWOPI * i / segments;
			Vec3d point = Add(state.Center, Add(Scale(u, Math.Cos(angle) * GetAxisLength(state)), Scale(v, Math.Sin(angle) * GetAxisLength(state))));
			if (!GetScreenPoint(state, point, out Vec2d projected)) continue;

			double distance = DistancePointToSegment(mouseX, mouseY, previous, projected);
			double dx = projected.X - previous.X;
			double dy = projected.Y - previous.Y;
			double length = Math.Sqrt(dx * dx + dy * dy);

			if (distance < best && length > 0.001)
			{
				best = distance;
				bestTangent = new Vec2d(dx / length, dy / length);
			}

			previous = projected;
		}

		if (best == double.MaxValue) { return false; }
		tangent = bestTangent;
		return true;
	}

	private Vec2d GetScreenAxis(GizmoState state, TFDesignAxis axis)
	{
		Vec3d end = Add(state.Center, Scale(GetWorldAxis(state, axis), GetAxisLength(state)));

		if (!GetScreenPoint(state, state.Center, out Vec2d a) || !GetScreenPoint(state, end, out Vec2d b)) { return new Vec2d(1, 0); }

		Vec2d vector = new(b.X - a.X, b.Y - a.Y);
		double length = Math.Sqrt(vector.X * vector.X + vector.Y * vector.Y);

		if (length < 0.001) { return new Vec2d(1, 0); }

		vector.X /= length;
		vector.Y /= length;
		return vector;
	}

	private bool GetScreenPoint(GizmoState state, Vec3d point, out Vec2d screenPos)
	{
		if (state.Ortho)
		{
			screenPos = new Vec2d(point.X, point.Y);
			return true;
		}

		return Project(point, out screenPos);
	}

	private bool Project(Vec3d worldPos, out Vec2d screenPos)
	{
		screenPos = new Vec2d();

		Vec3d projected = MatrixToolsd.Project(
			worldPos,
			ClientAPI.Render.PerspectiveProjectionMat,
			ClientAPI.Render.PerspectiveViewMat,
			ClientAPI.Render.FrameWidth,
			ClientAPI.Render.FrameHeight
		);

		if (projected.Z < 0) { return false; }

		screenPos.X = projected.X;
		screenPos.Y = ClientAPI.Render.FrameHeight - projected.Y;
		return true;
	}

	private bool TryGetMouseRay(int mouseX, int mouseY, out Vec3d origin, out Vec3d direction)
	{
		origin = new Vec3d();
		direction = new Vec3d();

		double[] projectionView = Mat4d.Create();
		Mat4d.Mul(projectionView, ClientAPI.Render.PerspectiveProjectionMat, ClientAPI.Render.PerspectiveViewMat);

		double[] inverse = Mat4d.Create();
		if (Mat4d.Invert(inverse, projectionView) is null) { return false; }

		if (!Unproject(inverse, mouseX, mouseY, -1, out Vec3d near)) return false;
		if (!Unproject(inverse, mouseX, mouseY, 1, out Vec3d far)) return false;

		direction = Sub(far, near);
		if (direction.LengthSq() < 0.000001) { return false; }

		origin = near;
		direction.Normalize();
		return true;
	}

	private bool Unproject(double[] inverseProjectionView, int mouseX, int mouseY, double clipZ, out Vec3d world)
	{
		world = new Vec3d();

		double ndcX = 2.0 * mouseX / ClientAPI.Render.FrameWidth - 1.0;
		double ndcY = 1.0 - 2.0 * mouseY / ClientAPI.Render.FrameHeight;

		double[] result = Mat4d.MulWithVec4(inverseProjectionView, new[] { ndcX, ndcY, clipZ, 1.0 });
		if (Math.Abs(result[3]) < 0.000001) { return false; }

		world.X = result[0] / result[3];
		world.Y = result[1] / result[3];
		world.Z = result[2] / result[3];
		return true;
	}

	private bool TryGetAxisCoordinate(Vec3d axisCenter, Vec3d axisDirection, int mouseX, int mouseY, out double coordinate)
	{
		coordinate = 0;

		if (!TryGetMouseRay(mouseX, mouseY, out Vec3d rayOrigin, out Vec3d rayDirection)) { return false; }

		Vec3d w0 = Sub(axisCenter, rayOrigin);

		double b = Dot(axisDirection, rayDirection);
		double d = Dot(axisDirection, w0);
		double e = Dot(rayDirection, w0);
		double denom = 1.0 - b * b;

		if (Math.Abs(denom) < 0.00001) { return false; }

		coordinate = (b * e - d) / denom;
		return true;
	}

	private bool TryGetRotateVector(Vec3d center, Vec3d normal, int mouseX, int mouseY, out Vec3d vector)
	{
		vector = new Vec3d();

		if (!TryIntersectMousePlane(center, normal, mouseX, mouseY, out Vec3d point)) { return false; }

		vector = Sub(point, center);
		vector = Sub(vector, Scale(normal, Dot(vector, normal)));

		if (vector.LengthSq() < 0.00001) { return false; }

		vector.Normalize();
		return true;
	}

	private bool TryIntersectMousePlane(Vec3d planePoint, Vec3d planeNormal, int mouseX, int mouseY, out Vec3d point)
	{
		point = new Vec3d();

		if (!TryGetMouseRay(mouseX, mouseY, out Vec3d rayOrigin, out Vec3d rayDirection)) { return false; }

		double denom = Dot(planeNormal, rayDirection);
		if (Math.Abs(denom) < 0.00001) { return false; }

		double distance = Dot(planeNormal, Sub(planePoint, rayOrigin)) / denom;
		if (distance < 0) { return false; }

		point = Add(rayOrigin, Scale(rayDirection, distance));
		return true;
	}

	private bool TryBuildState(ModelTransform? transform, out GizmoState state)
	{
		state = default;
		if (transform is null) return false;

		GetCameraBasis(out Vec3d forward, out Vec3d right, out Vec3d up);

		if (
			EditorUI.CurrentContext == TFDesignContext.Gui && 
			GUIPreviewRenderer.TryBuildGizmoState(transform, VSMCTFDModSystem.LocalSpace, out GuiPreviewGizmoState guiState)
		)
		{
			state = new GizmoState
			{
				Center = guiState.Center,
				AxisX = guiState.AxisX, AxisY = guiState.AxisY, AxisZ = guiState.AxisZ,
				CanonicalX = new Vec3d(1, 0, 0), CanonicalY = new Vec3d(0, 1, 0), CanonicalZ = new Vec3d(0, 0, 1),
				TranslationBasis = new TranslationBasis(guiState.TranslationBasisX, guiState.TranslationBasisY, guiState.TranslationBasisZ),
				RotationParentBasis = Matrix3.Identity, RotationParentBasisInverse = Matrix3.Identity,
				AttachmentRotation = new FastVec3f(0, 0, 0),
				CameraRight = new Vec3d(1, 0, 0), CameraUp = new Vec3d(0, -1, 0), CameraForward = new Vec3d(0, 0, 1),
				Ortho = true, GuiPreview = true,
				AxisLengthOverride = guiState.AxisLength,
				OrthoAxisLength = guiState.AxisLength, OrthoPixelsPerTransformUnit = guiState.PixelsPerTransformUnit,
				WorldUnitsPerPreviewPixel = guiState.WorldUnitsPerPreviewPixel
			};

			return true;
		}

		if (!TryGetRenderedCenter(transform, out Vec3d center)) { center = GetFallbackCenter(transform, forward, right, up); }

		TranslationBasis basis = BuildTranslationBasis(transform, center);

		if (!TryGetRotationSetup(transform, out Matrix3 rotationParentBasis, out FastVec3f attachmentRotation))
		{
			rotationParentBasis = Matrix3.Identity;
			attachmentRotation = new FastVec3f(0, 0, 0);
		}

		Matrix3 actualRotation = Matrix3.FromEulerDegrees(
			transform.Rotation.X + attachmentRotation.X,
			transform.Rotation.Y + attachmentRotation.Y,
			transform.Rotation.Z + attachmentRotation.Z
		);

		Matrix3 worldRotation = rotationParentBasis.Mul(actualRotation).Orthonormalized();

		Vec3d canonicalX = new(1, 0, 0);
		Vec3d canonicalY = new(0, 1, 0);
		Vec3d canonicalZ = new(0, 0, 1);

		Vec3d axisX = new(1, 0, 0);
		Vec3d axisY = new(0, 1, 0);
		Vec3d axisZ = new(0, 0, 1);

		if (VSMCTFDModSystem.LocalSpace)
		{
			axisX = worldRotation.TransformDirection(canonicalX).Normalize();
			axisY = worldRotation.TransformDirection(canonicalY).Normalize();
			axisZ = worldRotation.TransformDirection(canonicalZ).Normalize();
		}

		state = new GizmoState
		{
			Center = center,
			AxisX = SafeNormalize(axisX, new Vec3d(1, 0, 0)),
			AxisY = SafeNormalize(axisY, new Vec3d(0, 1, 0)),
			AxisZ = SafeNormalize(axisZ, new Vec3d(0, 0, 1)),
			CanonicalX = canonicalX,
			CanonicalY = canonicalY,
			CanonicalZ = canonicalZ,
			TranslationBasis = basis,
			RotationParentBasis = rotationParentBasis,
			RotationParentBasisInverse = rotationParentBasis.Inverted(),
			AttachmentRotation = attachmentRotation,
			CameraRight = right,
			CameraUp = up,
			CameraForward = forward,
			Ortho = false,
			GuiPreview = false,
			AxisLengthOverride = 0,
			OrthoAxisLength = AxisLength,
			OrthoPixelsPerTransformUnit = 1.0,
			WorldUnitsPerPreviewPixel = 1.0
		};

		return true;
	}

	private TranslationBasis BuildTranslationBasis(ModelTransform transform, Vec3d center)
	{
		Vec3d x = TryGetRenderedCenter(WithTranslationOffset(transform, 1, 0, 0), out Vec3d centerX)
			? Sub(centerX, center)
			: new Vec3d(transform.ScaleXYZ.X, 0, 0);

		Vec3d y = TryGetRenderedCenter(WithTranslationOffset(transform, 0, 1, 0), out Vec3d centerY)
			? Sub(centerY, center)
			: new Vec3d(0, transform.ScaleXYZ.Y, 0);

		Vec3d z = TryGetRenderedCenter(WithTranslationOffset(transform, 0, 0, 1), out Vec3d centerZ)
			? Sub(centerZ, center)
			: new Vec3d(0, 0, transform.ScaleXYZ.Z);

		return new TranslationBasis(x, y, z);
	}

	private ModelTransform WithTranslationOffset(ModelTransform transform, float x, float y, float z)
	{
		ModelTransform copy = transform.Clone();
		copy.Translation.X += x;
		copy.Translation.Y += y;
		copy.Translation.Z += z;
		return copy;
	}

	private bool TryGetRenderedCenter(ModelTransform transform, out Vec3d center)
	{
		center = new Vec3d();

		switch (EditorUI.CurrentContext)
		{
			case TFDesignContext.MainHand: return TryGetHeldItemCenter(transform, rightHand: true, out center);

			case TFDesignContext.OffHand: return TryGetHeldItemCenter(transform, rightHand: false, out center);

			case TFDesignContext.Ground:
			case TFDesignContext.Gui:
			default: return false;
		}
	}

	private bool TryGetRotationSetup(ModelTransform transform, out Matrix3 parentBasis, out FastVec3f attachmentRotation)
	{
		parentBasis = Matrix3.Identity;
		attachmentRotation = new FastVec3f(0, 0, 0);

		return EditorUI.CurrentContext switch
		{
			TFDesignContext.MainHand => TryGetHeldItemRotationSetup(transform, rightHand: true, out parentBasis, out attachmentRotation),
			TFDesignContext.OffHand => TryGetHeldItemRotationSetup(transform, rightHand: false, out parentBasis, out attachmentRotation),
			_ => true
		};
	}

	private bool TryGetHeldItemRotationSetup(ModelTransform transform, bool rightHand, out Matrix3 parentBasis, out FastVec3f attachmentRotation)
	{
		parentBasis = Matrix3.Identity;
		attachmentRotation = new FastVec3f(0, 0, 0);

		EntityPlayer playerEntity = ClientAPI.World.Player.Entity;
		AttachmentPointAndPose? apap = playerEntity.AnimManager?.Animator?.GetAttachmentPointPose(rightHand ? "RightHand" : "LeftHand");
		AttachmentPoint? ap = apap?.AttachPoint;
		if (apap is null || ap is null) { return false; }

		Matrixf matrix = new();
		BuildPlayerModelMatrix(matrix, playerEntity);
		matrix.Mul(apap.AnimModelMatrix);

		FastVec3f origin = transform.Origin;
		FastVec3f translation = transform.Translation;
		FastVec3f scale = transform.ScaleXYZ;

		matrix
			.Translate(origin.X, origin.Y, origin.Z)
			.Scale(scale.X, scale.Y, scale.Z)
			.Translate(ap.PosX / 16.0 + translation.X, ap.PosY / 16.0 + translation.Y, ap.PosZ / 16.0 + translation.Z);

		parentBasis = Matrix3.FromMatrixf(matrix);
		attachmentRotation = new FastVec3f((float)ap.RotationX, (float)ap.RotationY, (float)ap.RotationZ);
		return true;
	}

	private bool TryGetHeldItemCenter(ModelTransform transform, bool rightHand, out Vec3d center)
	{
		center = new Vec3d();

		EntityPlayer playerEntity = ClientAPI.World.Player.Entity;
		AttachmentPointAndPose? apap = playerEntity.AnimManager?.Animator?.GetAttachmentPointPose(rightHand ? "RightHand" : "LeftHand");
		AttachmentPoint? ap = apap?.AttachPoint;
		if (apap is null || ap is null) { return false; }

		Matrixf matrix = new();
		BuildPlayerModelMatrix(matrix, playerEntity);
		matrix.Mul(apap.AnimModelMatrix);

		FastVec3f origin = transform.Origin;
		FastVec3f translation = transform.Translation;
		FastVec3f rotation = transform.Rotation;
		FastVec3f scale = transform.ScaleXYZ;

		matrix
			.Translate(origin.X, origin.Y, origin.Z)
			.Scale(scale.X, scale.Y, scale.Z)
			.Translate(ap.PosX / 16.0 + translation.X, ap.PosY / 16.0 + translation.Y, ap.PosZ / 16.0 + translation.Z)
			.Rotate(
				(float)((ap.RotationX + rotation.X) * GameMath.DEG2RAD),
				(float)((ap.RotationY + rotation.Y) * GameMath.DEG2RAD),
				(float)((ap.RotationZ + rotation.Z) * GameMath.DEG2RAD)
			)
		.Translate(-origin.X, -origin.Y, -origin.Z);

		Vec4f relative = matrix.TransformVector(new Vec4f(0.5f, 0.5f, 0.5f, 1f));
		Vec3d camera = playerEntity.CameraPos;
		center = new Vec3d(camera.X + relative.X, camera.Y + relative.Y, camera.Z + relative.Z);
		return true;
	}

	private void BuildPlayerModelMatrix(Matrixf matrix, EntityPlayer playerEntity)
	{
		matrix.Identity();

		Vec3d camera = playerEntity.CameraPos;
		matrix.Translate(playerEntity.Pos.X - camera.X, playerEntity.Pos.InternalY - camera.Y, playerEntity.Pos.Z - camera.Z);

		float rotX = playerEntity.Properties.Client.Shape?.rotateX ?? 0f;
		float rotY = playerEntity.Properties.Client.Shape?.rotateY ?? 0f;
		float rotZ = playerEntity.Properties.Client.Shape?.rotateZ ?? 0f;

		matrix.Translate(0f, playerEntity.SelectionBox.Y2 / 2f, 0f);
		matrix.RotateX(playerEntity.Pos.Roll + rotX * GameMath.DEG2RAD);
		matrix.RotateY(playerEntity.BodyYaw + (90f + rotY) * GameMath.DEG2RAD);
		matrix.RotateZ(playerEntity.WalkPitch + rotZ * GameMath.DEG2RAD);
		matrix.Translate(0f, -playerEntity.SelectionBox.Y2 / 2f, 0f);

		float size = playerEntity.Properties.Client.Size;
		matrix.Scale(size, size, size);
		matrix.Translate(-0.5f, 0f, -0.5f);
	}

	private Vec3d GetFallbackCenter(ModelTransform transform, Vec3d forward, Vec3d right, Vec3d up)
	{
		Vec3d camera = ClientAPI.World.Player.Entity.CameraPos.Clone();
		Vec3d delta = new(
			transform.Translation.X * transform.ScaleXYZ.X,
			transform.Translation.Y * transform.ScaleXYZ.Y,
			transform.Translation.Z * transform.ScaleXYZ.Z
		);

		return EditorUI.CurrentContext switch
		{
			TFDesignContext.MainHand => Add(camera, Add(Add(Add(Scale(forward, 1.45), Scale(right, 0.45)), Scale(up, -0.35)), delta)),
			TFDesignContext.OffHand => Add(camera, Add(Add(Add(Scale(forward, 1.45), Scale(right, -0.45)), Scale(up, -0.35)), delta)),
			TFDesignContext.Ground when ClientAPI.World.Player.CurrentBlockSelection?.Position is not null => Add(new Vec3d(
				ClientAPI.World.Player.CurrentBlockSelection.Position.X + 0.5,
				ClientAPI.World.Player.CurrentBlockSelection.Position.Y + 1.05,
				ClientAPI.World.Player.CurrentBlockSelection.Position.Z + 0.5), delta
			),
			TFDesignContext.Ground => Add(camera, Add(Add(Scale(forward, 3.0), Scale(up, -0.5)), delta)),
			_ => Add(camera, Add(Add(Scale(forward, 2.0), Scale(up, 0.1)), delta))
		};
	}

	private void GetCameraBasis(out Vec3d forward, out Vec3d right, out Vec3d up)
	{
		double yaw = ClientAPI.World.Player.CameraYaw;
		double pitch = ClientAPI.World.Player.CameraPitch;

		forward = new Vec3d(-Math.Sin(yaw) * Math.Cos(pitch), Math.Sin(pitch), -Math.Cos(yaw) * Math.Cos(pitch)).Normalize();

		right = new Vec3d(Math.Cos(yaw), 0, -Math.Sin(yaw)).Normalize();
		up = right.Cross(forward).Normalize();

		if (up.LengthSq() < 0.0001) { up = new Vec3d(0, 1, 0); }
	}

	private static Vec3d GetWorldAxis(GizmoState state, TFDesignAxis axis)
	{
		return axis switch
		{
			TFDesignAxis.X => state.AxisX,
			TFDesignAxis.Y => state.AxisY,
			TFDesignAxis.Z => state.AxisZ,
			_ => state.AxisX
		};
	}

	private static Vec3d GetCanonicalAxis(TFDesignAxis axis)
	{
		return axis switch
		{
			TFDesignAxis.X => new Vec3d(1, 0, 0),
			TFDesignAxis.Y => new Vec3d(0, 1, 0),
			TFDesignAxis.Z => new Vec3d(0, 0, 1),
			_ => new Vec3d(1, 0, 0)
		};
	}

	private static Vec3d RotateCanonical(Vec3d vec, FastVec3f rotation)
	{
		Vec3d value = RotateX(vec, rotation.X * GameMath.DEG2RAD);
		value = RotateY(value, rotation.Y * GameMath.DEG2RAD);
		value = RotateZ(value, rotation.Z * GameMath.DEG2RAD);
		return value;
	}

	private static Vec3d RotateX(Vec3d vec, double radians)
	{
		double sin = Math.Sin(radians);
		double cos = Math.Cos(radians);
		return new Vec3d(vec.X, vec.Y * cos - vec.Z * sin, vec.Y * sin + vec.Z * cos);
	}

	private static Vec3d RotateY(Vec3d vec, double radians)
	{
		double sin = Math.Sin(radians);
		double cos = Math.Cos(radians);
		return new Vec3d(vec.X * cos + vec.Z * sin, vec.Y, -vec.X * sin + vec.Z * cos);
	}

	private static Vec3d RotateZ(Vec3d vec, double radians)
	{
		double sin = Math.Sin(radians);
		double cos = Math.Cos(radians);
		return new Vec3d(vec.X * cos - vec.Y * sin, vec.X * sin + vec.Y * cos, vec.Z);
	}

	private static Vec3d SafeNormalize(Vec3d value, Vec3d fallback)
	{
		if (value.LengthSq() < 0.000001) { return fallback; }
		return value.Normalize();
	}

	private static Vec3d SafeNormalize2D(Vec3d value, Vec3d fallback)
	{
		value.Z = 0;
		double length = Math.Sqrt(value.X * value.X + value.Y * value.Y);
		if (length < 0.000001) return fallback;

		value.X /= length;
		value.Y /= length;
		return value;
	}

	private static Vec3d Perpendicular(Vec3d direction, Vec3d seed)
	{
		Vec3d perp = direction.Cross(seed);
		if (perp.LengthSq() < 0.0001) { perp = direction.Cross(new Vec3d(1, 0, 0)); }
		if (perp.LengthSq() < 0.0001) { perp = direction.Cross(new Vec3d(0, 0, 1)); }

		return perp.Normalize();
	}

	private static double DistancePointToSegment(double x, double y, Vec2d a, Vec2d b)
	{
		double vx = b.X - a.X;
		double vy = b.Y - a.Y;
		double wx = x - a.X;
		double wy = y - a.Y;
		double lenSq = vx * vx + vy * vy;

		if (lenSq <= 0.0001) { return Math.Sqrt(wx * wx + wy * wy); }

		double t = Math.Clamp((wx * vx + wy * vy) / lenSq, 0, 1);
		double px = a.X + t * vx;
		double py = a.Y + t * vy;
		double dx = x - px;
		double dy = y - py;

		return Math.Sqrt(dx * dx + dy * dy);
	}

	private static double Dot(Vec3d left, Vec3d right)		{ return left.X * right.X + left.Y * right.Y + left.Z * right.Z; }
	private static Vec3d Add(Vec3d left, Vec3d right)		{ return new Vec3d(left.X + right.X, left.Y + right.Y, left.Z + right.Z); }
	private static Vec3d Sub(Vec3d left, Vec3d right)		{ return new Vec3d(left.X - right.X, left.Y - right.Y, left.Z - right.Z); }
	private static Vec3d Scale(Vec3d value, double scale)	{ return new Vec3d(value.X * scale, value.Y * scale, value.Z * scale); }

	public void Dispose()
	{
		ClientAPI.Event.MouseDown -= OnMouseDown;
		ClientAPI.Event.MouseMove -= OnMouseMove;
		ClientAPI.Event.MouseUp -= OnMouseUp;
		ClientAPI.Event.UnregisterRenderer(this, EnumRenderStage.Opaque);
		ClientAPI.Event.UnregisterRenderer(this, EnumRenderStage.Ortho);
	}

	private struct GizmoState
	{
		public Vec3d Center;
		public Vec3d AxisX, AxisY, AxisZ;
		public Vec3d CanonicalX, CanonicalY, CanonicalZ;
		public TranslationBasis TranslationBasis;
		public Matrix3 RotationParentBasis, RotationParentBasisInverse;
		public FastVec3f AttachmentRotation;
		public Vec3d CameraRight, CameraUp, CameraForward;
		public bool Ortho, GuiPreview;
		public double AxisLengthOverride;
		public double OrthoAxisLength, OrthoPixelsPerTransformUnit;
		public double WorldUnitsPerPreviewPixel;
	}

	private readonly struct Matrix3
	{
		public static Matrix3 Identity => new(
			1, 0, 0,
			0, 1, 0,
			0, 0, 1
		);

		private readonly double m00, m01, m02, m10, m11, m12, m20, m21, m22;
		public Matrix3(
			double m00, double m01, double m02,
			double m10, double m11, double m12,
			double m20, double m21, double m22
		)
		{
			this.m00 = m00; this.m01 = m01; this.m02 = m02; this.m10 = m10;
			this.m11 = m11; this.m12 = m12; this.m20 = m20; this.m21 = m21; this.m22 = m22;
		}

		public static Matrix3 FromMatrixf(Matrixf matrix)
		{
			float[] values = matrix.Values;
			return new Matrix3(
				values[0], values[4], values[8],
				values[1], values[5], values[9],
				values[2], values[6], values[10]
			);
		}

		public static Matrix3 FromEulerDegrees(double xDegrees, double yDegrees, double zDegrees)
		{
			double x = xDegrees * GameMath.DEG2RAD;
			double y = yDegrees * GameMath.DEG2RAD;
			double z = zDegrees * GameMath.DEG2RAD;

			double sx = Math.Sin(x);
			double cx = Math.Cos(x);
			double sy = Math.Sin(y);
			double cy = Math.Cos(y);
			double sz = Math.Sin(z);
			double cz = Math.Cos(z);

			return new Matrix3(
				cy * cz,
				-cy * sz,
				sy,

				cx * sz + sx * sy * cz,
				cx * cz - sx * sy * sz,
				-sx * cy,

				-cx * sy * cz + sx * sz,
				cx * sy * sz + sx * cz,
				cx * cy
			);
		}

		public static Matrix3 FromAxisAngle(Vec3d axis, double radians)
		{
			axis = SafeNormalize(new Vec3d(axis.X, axis.Y, axis.Z), new Vec3d(1, 0, 0));

			double x = axis.X;
			double y = axis.Y;
			double z = axis.Z;
			double s = Math.Sin(radians);
			double c = Math.Cos(radians);
			double t = 1.0 - c;

			return new Matrix3(
				t * x * x + c,
				t * x * y - s * z,
				t * x * z + s * y,

				t * x * y + s * z,
				t * y * y + c,
				t * y * z - s * x,

				t * x * z - s * y,
				t * y * z + s * x,
				t * z * z + c
			);
		}

		public Matrix3 Mul(Matrix3 right)
		{
			return new Matrix3(
				m00 * right.m00 + m01 * right.m10 + m02 * right.m20,
				m00 * right.m01 + m01 * right.m11 + m02 * right.m21,
				m00 * right.m02 + m01 * right.m12 + m02 * right.m22,

				m10 * right.m00 + m11 * right.m10 + m12 * right.m20,
				m10 * right.m01 + m11 * right.m11 + m12 * right.m21,
				m10 * right.m02 + m11 * right.m12 + m12 * right.m22,

				m20 * right.m00 + m21 * right.m10 + m22 * right.m20,
				m20 * right.m01 + m21 * right.m11 + m22 * right.m21,
				m20 * right.m02 + m21 * right.m12 + m22 * right.m22
			);
		}

		public Matrix3 Inverted()
		{
			double determinant =
				m00 * (m11 * m22 - m12 * m21) -
				m01 * (m10 * m22 - m12 * m20) +
				m02 * (m10 * m21 - m11 * m20);

			if (Math.Abs(determinant) < 0.000001) { return Identity; }

			double inv = 1.0 / determinant;

			return new Matrix3(
				(m11 * m22 - m12 * m21) * inv,
				(m02 * m21 - m01 * m22) * inv,
				(m01 * m12 - m02 * m11) * inv,

				(m12 * m20 - m10 * m22) * inv,
				(m00 * m22 - m02 * m20) * inv,
				(m02 * m10 - m00 * m12) * inv,

				(m10 * m21 - m11 * m20) * inv,
				(m01 * m20 - m00 * m21) * inv,
				(m00 * m11 - m01 * m10) * inv
			);
		}

		public Matrix3 Orthonormalized()
		{
			Vec3d x = new(m00, m10, m20);
			Vec3d y = new(m01, m11, m21);

			x = SafeNormalize(x, new Vec3d(1, 0, 0));
			y = Sub(y, Scale(x, Dot(y, x)));
			y = SafeNormalize(y, Perpendicular(x, new Vec3d(0, 1, 0)));

			Vec3d z = x.Cross(y);
			z = SafeNormalize(z, new Vec3d(0, 0, 1));
			y = z.Cross(x);
			y = SafeNormalize(y, new Vec3d(0, 1, 0));

			return FromColumns(x, y, z);
		}

		public Vec3d TransformDirection(Vec3d direction)
		{
			return new Vec3d(
				m00 * direction.X + m01 * direction.Y + m02 * direction.Z,
				m10 * direction.X + m11 * direction.Y + m12 * direction.Z,
				m20 * direction.X + m21 * direction.Y + m22 * direction.Z
			);
		}

		public Vec3d ToEulerDegrees()
		{
			double y = Math.Asin(Math.Clamp(m02, -1.0, 1.0));
			double cy = Math.Cos(y);
			double x;
			double z;

			if (Math.Abs(cy) > 0.00001)
			{
				x = Math.Atan2(-m12, m22);
				z = Math.Atan2(-m01, m00);
			}
			else
			{
				z = 0;

				if (m02 > 0) { x = Math.Atan2(m10, m11); }
				else { x = Math.Atan2(-m10, m11); }
			}

			return new Vec3d(
				x * GameMath.RAD2DEG,
				y * GameMath.RAD2DEG,
				z * GameMath.RAD2DEG
			);
		}

		private static Matrix3 FromColumns(Vec3d x, Vec3d y, Vec3d z)
		{
			return new Matrix3(
				x.X, y.X, z.X,
				x.Y, y.Y, z.Y,
				x.Z, y.Z, z.Z
			);
		}
	}

	private readonly struct TranslationBasis
	{
		private readonly Vec3d x;
		private readonly Vec3d y;
		private readonly Vec3d z;
		private readonly double determinant;

		public TranslationBasis(Vec3d x, Vec3d y, Vec3d z)
		{
			this.x = x;
			this.y = y;
			this.z = z;

			determinant =
				x.X * (y.Y * z.Z - y.Z * z.Y) -
				y.X * (x.Y * z.Z - x.Z * z.Y) +
				z.X * (x.Y * y.Z - x.Z * y.Y);
		}

		public Vec3d TransformDirection(Vec3d transformDirection)
		{
			return Add(Add(Scale(x, transformDirection.X), Scale(y, transformDirection.Y)), Scale(z, transformDirection.Z));
		}

		public Vec3d WorldToTransformDelta(Vec3d worldDelta)
		{
			if (Math.Abs(determinant) < 0.000001) { return FallbackWorldToTransformDelta(worldDelta); }

			double tx =
				worldDelta.X * (y.Y * z.Z - y.Z * z.Y) -
				y.X * (worldDelta.Y * z.Z - worldDelta.Z * z.Y) +
				z.X * (worldDelta.Y * y.Z - worldDelta.Z * y.Y);

			double ty =
				x.X * (worldDelta.Y * z.Z - worldDelta.Z * z.Y) -
				worldDelta.X * (x.Y * z.Z - x.Z * z.Y) +
				z.X * (x.Y * worldDelta.Z - x.Z * worldDelta.Y);

			double tz =
				x.X * (y.Y * worldDelta.Z - y.Z * worldDelta.Y) -
				y.X * (x.Y * worldDelta.Z - x.Z * worldDelta.Y) +
				worldDelta.X * (x.Y * y.Z - x.Z * y.Y);

			return new Vec3d(tx / determinant, ty / determinant, tz / determinant);
		}

		private Vec3d FallbackWorldToTransformDelta(Vec3d worldDelta)
		{
			double dx = ProjectOntoBasis(worldDelta, x);
			double dy = ProjectOntoBasis(worldDelta, y);
			double dz = ProjectOntoBasis(worldDelta, z);
			return new Vec3d(dx, dy, dz);
		}

		private static double ProjectOntoBasis(Vec3d worldDelta, Vec3d basis)
		{
			double lengthSq = basis.LengthSq();
			if (lengthSq < 0.000001) return 0;
			return Dot(worldDelta, basis) / lengthSq;
		}
	}
}
