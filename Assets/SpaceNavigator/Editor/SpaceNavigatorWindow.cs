//#define DEBUG_LEVEL_LOG
//#define DEBUG_LEVEL_WARN
//#define DEBUG_LEVEL_ERROR

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

[Serializable]
public class SpaceNavigatorWindow : EditorWindow {
	public enum OperationMode { Navigation, FreeMove, GrabMove }
	public OperationMode NavigationMode;
	public enum CoordinateSystem { Camera, World, Parent, Local }
	public static CoordinateSystem CoordSys;

	// Rig components
	[SerializeField]
	private GameObject _pivotGO, _cameraGO;
	[SerializeField]
	private Transform _pivot, _camera;

	// Snapping
	private Dictionary<Transform, Quaternion> _unsnappedRotations = new Dictionary<Transform, Quaternion>();
	private Dictionary<Transform, Vector3> _unsnappedTranslations = new Dictionary<Transform, Vector3>();
	private bool _snapRotation;
	public int SnapAngle = 45;
	private bool _snapTranslation;
	public float SnapDistance = 0.1f;

	// Settings
	private const string ModeKey = "Navigation mode";
	public const OperationMode NavigationModeDefault = OperationMode.Navigation;

	/// <summary>
	/// Initializes the window.
	/// </summary>
	[MenuItem("Window/Space Navigator &s")]
	public static void Init() {
		SpaceNavigatorWindow window = GetWindow(typeof(SpaceNavigatorWindow)) as SpaceNavigatorWindow;

		if (window) {
			window.Show();
		}
	}
	public void OnEnable() {
		ReadSettings();
		InitCameraRig();
		StoreSelection();
	}
	/// <summary>
	/// Called when window is closed.
	/// </summary>
	public void OnDestroy() {
		WriteSettings();
		DisposeCameraRig();
		SpaceNavigator.Instance.Dispose();
	}
	public void OnDisable() {
		WriteSettings();
		SpaceNavigator.Instance.Dispose();
	}
	public void OnSelectionChange() {
		StoreSelection();
	}

	public void ReadSettings() {
		NavigationMode = (OperationMode)EditorPrefs.GetInt(ModeKey, (int)NavigationModeDefault);

		SpaceNavigator.Instance.ReadSettings();
	}
	private void WriteSettings() {
		EditorPrefs.SetInt(ModeKey, (int)NavigationMode);

		SpaceNavigator.Instance.WriteSettings();
	}

	/// <summary>
	/// Sets up a dummy camera rig like the scene camera.
	/// We can't move the camera, only the SceneView's pivot & rotation.
	/// For some reason the camera does not always have the same position offset to the pivot.
	/// This offset is unpredictable, so we have to update our dummy rig each time before using it.
	/// </summary>
	private void InitCameraRig() {
		// Create camera rig if one is not already present.
		if (!_pivotGO) {
			_cameraGO = new GameObject("Scene camera dummy") { hideFlags = HideFlags.HideAndDontSave };
			_camera = _cameraGO.transform;

			_pivotGO = new GameObject("Scene camera pivot dummy") { hideFlags = HideFlags.HideAndDontSave };
			_pivot = _pivotGO.transform;
			_pivot.parent = _camera;
		}

		SyncRigWithScene();
	}
	/// <summary>
	/// Position the dummy camera rig like the scene view camera.
	/// </summary>
	private void SyncRigWithScene() {
		if (SceneView.lastActiveSceneView) {
			_camera.position = SceneView.lastActiveSceneView.camera.transform.position;	// <- this value changes w.r.t. pivot !
			_camera.rotation = SceneView.lastActiveSceneView.camera.transform.rotation;
			_pivot.position = SceneView.lastActiveSceneView.pivot;
			_pivot.rotation = SceneView.lastActiveSceneView.rotation;
		}
	}
	private void DisposeCameraRig() {
		DestroyImmediate(_cameraGO);
		DestroyImmediate(_pivotGO);
	}

	/// <summary>
	/// This is called 100x per second (if the window content is visible).
	/// </summary>
	public void Update() {
		SceneView sceneView = SceneView.lastActiveSceneView;
		if (!sceneView) return;

		// Return if device is idle.
		if (SpaceNavigator.Translation == Vector3.zero &&
			SpaceNavigator.Rotation == Quaternion.identity)
			return;
	
		switch (NavigationMode) {
			case OperationMode.Navigation:
				Navigate(sceneView);
				break;
			case OperationMode.FreeMove:
				// Manipulate the object free from the camera.
				FreeMove(sceneView);
				break;
			case OperationMode.GrabMove:
				// Manipulate the object together with the camera.
				GrabMove(sceneView);
				break;
			default:
				throw new ArgumentOutOfRangeException();
		}

		//// Detect keyboard clicks (not working).
		//if (Keyboard.IsKeyDown(1))
		//	D.log("Button 0 pressed");
		//if (Keyboard.IsKeyDown(2))
		//	D.log("Button 1 pressed");
	}

	private void Navigate(SceneView sceneView) {
		SyncRigWithScene();

		_camera.Translate(SpaceNavigator.Translation, Space.Self);

		//// Default rotation method, applies the whole quaternion to the camera.
		//Quaternion sceneCamera = sceneView.camera.transform.rotation;
		//Quaternion inputInWorldSpace = RotationInWorldSpace;
		//Quaternion inputInCameraSpace = sceneCamera * inputInWorldSpace * Quaternion.Inverse(sceneCamera);
		//_camera.rotation = inputInCameraSpace * _camera.rotation;

		// This method keeps the horizon horizontal at all times.
		// Perform azimuth in world coordinates.
		_camera.RotateAround(Vector3.up, Yaw(SpaceNavigator.Rotation));
		// Perform pitch in local coordinates.
		_camera.RotateAround(_camera.right, Pitch(SpaceNavigator.Rotation));

		// Update sceneview pivot and repaint view.
		sceneView.pivot = _pivot.position;
		sceneView.rotation = _pivot.rotation;
		sceneView.Repaint();
	}
	private void FreeMove(SceneView sceneView) {
		Transform reference;
		foreach (Transform transform in Selection.GetTransforms(SelectionMode.TopLevel | SelectionMode.Editable)) {
			switch (CoordSys) {
				case CoordinateSystem.Camera:
					reference = sceneView.camera.transform;
					break;
				case CoordinateSystem.World:
					reference = null;
					break;
				case CoordinateSystem.Parent:
					reference = transform.parent;
					break;
				case CoordinateSystem.Local:
					reference = transform;
					break;
				default:
					throw new ArgumentOutOfRangeException();
			}

			if (!_unsnappedRotations.ContainsKey(transform)) return;

			if (reference == null) {
				// Translate the object in world coordinates.
				transform.Translate(SpaceNavigator.Translation, Space.World);

				// Calculate the rotation in world coordinates.
				_unsnappedRotations[transform] = SpaceNavigator.Rotation*_unsnappedRotations[transform];
			} else {
				// Translate the selected object in reference coordinate system.
				Vector3 worldTranslation = reference.TransformPoint(SpaceNavigator.Translation) -
										   reference.position;
				transform.Translate(worldTranslation, Space.World);

				// Calculate the rotation in the reference coordinate system.
				_unsnappedRotations[transform] = SpaceNavigator.RotationInLocalCoordSys(reference) * _unsnappedRotations[transform];
			}

			// Perform rotation with or without snapping.
			transform.rotation = _snapRotation ? SnapRotation(_unsnappedRotations[transform], SnapAngle) : _unsnappedRotations[transform];
		}
	}
	private void GrabMove(SceneView sceneView) {
		foreach (Transform transform in Selection.GetTransforms(SelectionMode.TopLevel | SelectionMode.Editable)) {
			// Rotate yaw around world Y axis.
			transform.RotateAround(_camera.position, Vector3.up, Yaw(SpaceNavigator.Rotation) * Mathf.Rad2Deg);
			// Rotate pitch around camera right axis.
			transform.RotateAround(_camera.position, _camera.right, Pitch(SpaceNavigator.Rotation) * Mathf.Rad2Deg);
			// Translate in camera space.
			Vector3 worldTranslation = sceneView.camera.transform.TransformPoint(SpaceNavigator.Translation) -
										sceneView.camera.transform.position;
			transform.Translate(worldTranslation, Space.World);
		}

		Navigate(sceneView);
	}

	/// <summary>
	/// Draws the EditorWindow's GUI.
	/// </summary>
	public void OnGUI() {
		GUILayout.BeginVertical();
		GUILayout.Label("Operation mode");
		string[] buttons = new string[] { "Navigate", "Free move", "Grab move" };
		NavigationMode = (OperationMode)GUILayout.SelectionGrid((int)NavigationMode, buttons, 3);

		SceneView sceneView = SceneView.lastActiveSceneView;
		if (GUILayout.Button("Reset camera")) {
			if (sceneView) {
				sceneView.pivot = new Vector3(0, 0, 0);
				sceneView.rotation = Quaternion.identity;
				sceneView.Repaint();
			}
		}

		GUILayout.Label("Coordinate system");
		buttons = new string[] { "Camera", "World", "Parent", "Local" };
		CoordSys = (CoordinateSystem)GUILayout.SelectionGrid((int)CoordSys, buttons, 4);

		GUILayout.BeginHorizontal();
		_snapRotation = GUILayout.Toggle(_snapRotation, "Angle snapping");
		string angleText = GUILayout.TextField(SnapAngle.ToString());
		int newSnapAngle;
		if (int.TryParse(angleText, out newSnapAngle)) {
			SnapAngle = newSnapAngle;
		}
		GUILayout.EndHorizontal();

		//GUILayout.BeginHorizontal();
		//_snapTranslation = GUILayout.Toggle(_snapTranslation, "Distance snapping");
		//string distanceText = GUILayout.TextField(SnapDistance.ToString());
		//int newSnapDistance;
		//if (int.TryParse(distanceText, out newSnapDistance)) {
		//	SnapDistance = newSnapDistance;
		//}
		//GUILayout.EndHorizontal();

		SpaceNavigator.Instance.OnGUI();

		GUILayout.EndVertical();
	}

	#region - Snapping -
	public void StoreSelection() {
		_unsnappedRotations.Clear();
		_unsnappedTranslations.Clear();
		foreach (Transform transform in Selection.GetTransforms(SelectionMode.TopLevel | SelectionMode.Editable)) {
			_unsnappedRotations.Add(transform, transform.rotation);
			_unsnappedTranslations.Add(transform, transform.position);
		}
	}
	private Quaternion SnapRotation(Quaternion q, float snap) {
		Vector3 euler = q.eulerAngles;
		return Quaternion.Euler(
			Mathf.RoundToInt(euler.x / snap) * snap,
			Mathf.RoundToInt(euler.y / snap) * snap,
			Mathf.RoundToInt(euler.z / snap) * snap);
	}
	private Vector3 SnapTranslation(Vector3 v, float snap) {
		return new Vector3(
			Mathf.RoundToInt(v.x / snap) * snap,
			Mathf.RoundToInt(v.y / snap) * snap,
			Mathf.RoundToInt(v.z / snap) * snap);
	}
	#endregion - Snapping -

	#region - Quaternion helpers -
	// Found this code online: http://sunday-lab.blogspot.nl/2008/04/get-pitch-yaw-roll-from-quaternion.html
	float Pitch(Quaternion q) {
		return Mathf.Atan2(2 * (q.y * q.z + q.w * q.x), q.w * q.w - q.x * q.x - q.y * q.y + q.z * q.z);
	}
	float Yaw(Quaternion q) {
		return Mathf.Asin(-2 * (q.x * q.z - q.w * q.y));
	}
	float Roll(Quaternion q) {
		return Mathf.Atan2(2 * (q.x * q.y + q.w * q.z), q.w * q.w + q.x * q.x - q.y * q.y - q.z * q.z);
	}
	#endregion - Quaternion helpers -
}