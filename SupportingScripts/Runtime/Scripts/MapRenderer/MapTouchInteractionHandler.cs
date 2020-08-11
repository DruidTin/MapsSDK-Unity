using Microsoft.Geospatial;
using Microsoft.Maps.Unity;
using System;
using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// An event that provides a <see cref="LatLonAlt"/>.
/// </summary>
[Serializable]
public class LatLonAltUnityEvent : UnityEvent<LatLonAlt> { }

/// <summary>
/// Handles touch-screen based interactions like pan, pinch to zoom, double tap zoom, and tap-and-hold.
/// </summary>
[RequireComponent(typeof(MapInteractionController))]
[RequireComponent(typeof(MapRenderer))]
public class MapTouchInteractionHandler : MonoBehaviour
{
    private const float MinTapAndHoldDurationInSeconds = 1.0f;

    private MapRenderer _mapRenderer;
    private MapInteractionController _mapInteractionController;

    private bool _isInteracting;
    private MercatorCoordinate _interactionTargetCoordinate;
    private double _interactionTargetAltitude;

    private float _dpiScale = 1.0f;
    private int _lastTouchCount;
    private float _initialTouchPointDelta;
    private Vector2 _initialTouchPoint;
    private double _initialMapDimensionInMercator;
    private float _tapAndHoldBeginTime = float.MaxValue;

    /// <summary>
    /// The <see cref="Camera"/> associated with the interactions. Defaults to <see cref="Camera.main"/>.
    /// </summary>
    [SerializeField]
    private Camera _camera = null;

    [SerializeField]
    private LatLonAltUnityEvent _onTapAndHold = new LatLonAltUnityEvent();

    [SerializeField]
    private LatLonAltUnityEvent _onDoubleTap = new LatLonAltUnityEvent();

    [SerializeField]
    private UnityEvent _onInteractionStarted = new UnityEvent();

    [SerializeField]
    private UnityEvent _onInteractionEnded = new UnityEvent();

    private void Awake()
    {
        _dpiScale = Mathf.Max(1.0f, Screen.dpi / 96.0f);

        _mapRenderer = GetComponent<MapRenderer>();
        _mapInteractionController = GetComponent<MapInteractionController>();

        if (_camera == null)
        {
            _camera = Camera.main;
        }
    }

    private void Update()
    {
        var touchCount = Input.touchCount;
        if (touchCount == 0)
        {
            if (_isInteracting)
            {
                _isInteracting = false;
                _onInteractionEnded.Invoke();
            }

            _initialTouchPointDelta = 0.0f;
            _lastTouchCount = touchCount;

            return;
        }

        var touch0 = Input.GetTouch(0);
        var touchPoint = touch0.position;
        var touchPointDelta = 0.0f; // Used when there are two touch points (for pinch/zoom).

        // A single touch point is a pan, a double-tap, or a tap-and-hold.
        if (touchCount == 1)
        {
            // Disable zoom with two touch points.
            _initialTouchPointDelta = 0.0f;

            // Check for a double tap.
            if (touch0.phase == TouchPhase.Ended && touch0.tapCount > 1)
            {
                // Do a double tap and early out.
                var ray = _camera.ScreenPointToRay(touchPoint);
                if (_mapRenderer.Raycast(ray, out var hitInfo))
                {
                    var newZoomLevel = _mapRenderer.ZoomLevel + 1.0f;
                    newZoomLevel = Mathf.Max(_mapRenderer.MinimumZoomLevel, Mathf.Min(_mapRenderer.MaximumZoomLevel, newZoomLevel));
                    _mapRenderer.SetMapScene(new MapSceneOfLocationAndZoomLevel(hitInfo.Location.LatLon, newZoomLevel), MapSceneAnimationKind.Linear, 150.0f);

                    _onDoubleTap.Invoke(hitInfo.Location);
                }

                _tapAndHoldBeginTime = float.MaxValue; // Reset tap and hold.
                _lastTouchCount = 0; // Reset interactions.

                return;
            }

            // Check for tap and hold.
            if (_isInteracting)
            {
                // If we're in the middle of an interaction (in this case, a pan), tap and hold doesn't apply.
                if (_tapAndHoldBeginTime != float.MaxValue)
                {
                    _tapAndHoldBeginTime = float.MaxValue;
                }
            }
            else
            {
                // Track tap and hold.
                if (touch0.phase == TouchPhase.Began)
                {
                    _tapAndHoldBeginTime = Time.time;
                }
                else if (!HasMovedTouchPointDeltaFromInitial(touchPoint))
                {
                    // The touch point has not moved enough to start an interaction, so we are in a stationary tap.
                    // Fire off the TapAndHold event once we exceed the hold threshold.
                    if ((Time.time - _tapAndHoldBeginTime) >= MinTapAndHoldDurationInSeconds)
                    {
                        var ray = _camera.ScreenPointToRay(touchPoint);
                        if (_mapRenderer.Raycast(ray, out var hitInfo))
                        {
                            _onTapAndHold.Invoke(hitInfo.Location);

                            // Reset so we don't continually fire off TapAndHold events on subsequent frames where touch point continues
                            // to be stationary.
                            _tapAndHoldBeginTime = float.MaxValue;
                        }
                    }
                }

                // Reset tap and hold state if this touch has been ended or cancelled.
                if (touch0.phase == TouchPhase.Canceled || touch0.phase == TouchPhase.Ended)
                {
                    _tapAndHoldBeginTime = float.MaxValue;
                }
            }
        }
        else // Touch count > 1, start tracking pinch.
        {
            // Touch delta can be calculated from first and second points.
            touchPointDelta = (Input.GetTouch(1).position - touch0.position).magnitude;

            // Touch point will be average between first and second.
            touchPoint += Input.GetTouch(1).position;
            touchPoint *= 0.5f;

            _tapAndHoldBeginTime = float.MinValue; // Reset tap and hold.
        }

        // In case a second touch point is added or removed, or more generally, if this is the first frame of any touch interaction,
        // we'll need to reset the target coordiante... This involves raycasting the map to see where the touch initally hits.
        var resetInteractionTarget = _lastTouchCount != touchCount;
        if (resetInteractionTarget)
        {
            var ray = _camera.ScreenPointToRay(touchPoint);
            if (_mapRenderer.Raycast(ray, out var hitInfo))
            {
                // We have a hit, so set up some initial variables.
                _interactionTargetCoordinate = hitInfo.Location.LatLon.ToMercatorCoordinate();
                _interactionTargetAltitude = hitInfo.Location.AltitudeInMeters;
                _initialTouchPointDelta = 0.0f;
                _initialTouchPoint = touchPoint;
                _lastTouchCount = touchCount;
            }
            else
            {
                // The touch point didn't hit the map, so end any active interactions.
                if (_isInteracting)
                {
                    _lastTouchCount = 0;
                    _isInteracting = false;
                    _onInteractionEnded.Invoke();
                }
            }
        }
        else
        {
            // If we've made it to this case, the touch point has hit the map and we're either waiting for a movement
            // to occur which exceeds the touch interaction thresholds, or a movement is actively happening and we
            // need to update the map's state (i.e., the center and zoom properties).

            var touchPointDeltaToInitialDeltaRatio = touchPointDelta / _initialTouchPointDelta;

            if (!_isInteracting)
            {
                // Check if the initial interaction touch points have moved past a threshold to consider this a pan or zoom.
                if (touchPointDeltaToInitialDeltaRatio > 1.05 || HasMovedTouchPointDeltaFromInitial(touchPoint))
                {
                    _isInteracting = true;
                    _onInteractionStarted.Invoke();
                }
            }

            if (_isInteracting)
            {
                // We're inside an intersection, handle pinch/zoom first.
                if (touchPointDelta > 0.0f)
                {
                    var isInitialZoomFrame = _initialTouchPointDelta == 0.0;
                    if (isInitialZoomFrame)
                    {
                        _initialTouchPointDelta = touchPointDelta;
                        _initialMapDimensionInMercator = Mathf.Pow(2, _mapRenderer.ZoomLevel - 1);
                    }
                    else
                    {
                        var newMapDimensionInMercator = touchPointDeltaToInitialDeltaRatio * _initialMapDimensionInMercator;
                        var newZoomLevel = Math.Log(newMapDimensionInMercator) / Math.Log(2) + 1;
                        _mapRenderer.ZoomLevel = Mathf.Clamp((float)newZoomLevel, _mapRenderer.MinimumZoomLevel, _mapRenderer.MaximumZoomLevel);
                    }
                }

                // Handle panning last. Zoom is handled above in the pinch case, and it should be updated prior to panning.
                {
                    var ray = _camera.ScreenPointToRay(touchPoint);
                    _mapInteractionController.PanAndZoom(ray, _interactionTargetCoordinate, _interactionTargetAltitude, 0.0f);
                }
            }
        }
    }

    private bool HasMovedTouchPointDeltaFromInitial(Vector2 touchPoint)
    {
        return (_initialTouchPoint - touchPoint).magnitude > 5 /* logic px */ * _dpiScale;
    }
}
