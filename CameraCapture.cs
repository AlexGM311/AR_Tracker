using System;
using System.Collections.Generic;
using UnityEngine;
using OpenCVForUnity.CoreModule;
using OpenCVForUnity.ImgprocModule;
using OpenCVForUnity.TrackingModule;
using OpenCVForUnity.UnityUtils;
using OpenCVForUnity.VideoModule;
using UnityEngine.Experimental.Rendering;
using UnityEngine.UI;
using Rect = OpenCVForUnity.CoreModule.Rect;

public class CameraCapture : MonoBehaviour
{
    public RawImage display;

    Mat frameMat;
    Texture2D texture;

    private Vector2 startMousePos;
    private Vector2 endMousePos;
    private bool isSelecting = false;
    private Rect2d trackingRect;
    private List<Tracker> trackers = new List<Tracker>();
    private bool buttonDown = false;
    private bool buttonPressed = false;
    private List<Rect> bboxes = new List<Rect>();
    public static RenderTexture SendTexture;
    public static Size Dimensions = new (720, 402);

    public void Start()
    {
        Screen.SetResolution((int)Dimensions.width, (int)Dimensions.height, false);
        // testImage = Resources.Load<Texture2D>("7 1");
        SendTexture = new RenderTexture(640, 360, 0, GraphicsFormat.B8G8R8A8_SRGB);
        texture = new Texture2D((int)Dimensions.width, (int)Dimensions.height, TextureFormat.RGB24, false);
        frameMat = new Mat((int)Dimensions.height, (int)Dimensions.width    , CvType.CV_8UC3);
    }

    public void OnSelectButtonClicked()
    {
        buttonDown = true;
        buttonPressed = true;
        foreach (var tracker in trackers)
            tracker.Dispose();
        trackers.Clear();
        bboxes.Clear();
        Debug.Log("Начат процесс выбора");
    }
    
    void ResizeTexture()
    {
        RenderTexture rt = RenderTexture.GetTemporary((int)Dimensions.width, (int)Dimensions.height);
        RenderTexture.active = rt;
        Graphics.Blit(WebRtcManager.RemoteTexture, rt);
        texture.ReadPixels(new UnityEngine.Rect(0, 0, (int)Dimensions.width, (int)Dimensions.height), 0, 0);
        texture.Apply();
    
        RenderTexture.active = null;
        RenderTexture.ReleaseTemporary(rt);
    }
    
    void Update()
    {
        if (!WebRtcManager.RemoteTexture)
        {
            // Debug.Log("Remote texture is null");
            return;
        }
        if (WebRtcManager.RemoteTexture.width <= 0 || 
            WebRtcManager.RemoteTexture.height <= 0)
        {
            Debug.LogWarning("Invalid texture dimensions received");
            return;
        }
        ResizeTexture();
        Utils.texture2DToMat(texture, frameMat);
        
        if (!display.texture)
            display.texture = texture;
        // ======== Трекинг ========
        if (bboxes.Count > 0)
        {
            for (int i = 0; i < trackers.Count; i++)
            {
                Rect updatedBox = new Rect();
                bool ok = trackers[i].update(frameMat, updatedBox);
                if (ok)
                {
                    bboxes[i] = updatedBox;
                    Imgproc.rectangle(frameMat, updatedBox.tl(), updatedBox.br(), new Scalar(255, 0, 0, 255), 2);
                }
                else
                {
                    Debug.Log("Not tracking");
                }
            }
        }
        // Получаем координаты мыши или тача
    #if UNITY_ANDROID && !UNITY_EDITOR
        bool pressed = false;
        Vector2 mousePos = new Vector2(-100000, -100000);
        if (Input.touchCount > 0) 
        {
            Touch touch = Input.GetTouch(0); // Safe to access now
            mousePos = touch.position;
            pressed = (touch.phase == TouchPhase.Began || touch.phase == TouchPhase.Moved);
        
            // Now use touchPos and pressed safely
            if (pressed) 
            {
                Debug.Log("Touch at: " + mousePos);
            }
        }
    #else
        Vector2 mousePos = Input.mousePosition;
        bool pressed = Input.GetMouseButton(0);
    #endif

        // Преобразуем координаты мыши в координаты в пределах изображения камеры
        Vector2 localPoint;
        RectTransform rt = display.rectTransform;

        if (RectTransformUtility.ScreenPointToLocalPointInRectangle(rt, mousePos, null, out localPoint))
        {
            float x = localPoint.x + rt.rect.width / 2f;
            float y = localPoint.y + rt.rect.height / 2f;

            double scaleX = Dimensions.width / rt.rect.width;
            double scaleY = Dimensions.height / rt.rect.height;

            double camX = x * scaleX;
            double camY = y * scaleY;

            mousePos = new Vector2((float)camX, (float)camY);
        }
        else
        {
            mousePos = new Vector2(-1e9f, -1e9f); // вне RawImage — пропускаем кадр
        }
        if (buttonDown && !pressed) buttonDown = false;
        else if (buttonDown || !buttonPressed) {/*Debug.Log("Quitting early");*/} // Если прошлый if не сработал, а кнопка нажата
        else if (!isSelecting && pressed)
        {
            Debug.Log($"Started selection from ({mousePos.x}, {mousePos.y})");
            isSelecting = true;
            startMousePos = mousePos;
        }
        // ======== Обработка выбора области ========
        else if (isSelecting && pressed)
        {
            endMousePos = mousePos;
            Point p1 = new Point(startMousePos.x, frameMat.height() - startMousePos.y);
            Point p2 = new Point(endMousePos.x, frameMat.height() - endMousePos.y);
            Imgproc.rectangle(frameMat, p1, p2, new Scalar(0, 255, 0, 255), 2);
        }
        else if (!pressed && isSelecting)
        {
            isSelecting = false;

            float dx = Mathf.Abs(endMousePos.x - startMousePos.x);
            float dy = Mathf.Abs(endMousePos.y - startMousePos.y);

            if (dx < 10 || dy < 10)  // <== Порог можно подкорректировать
            {
                Debug.Log("Ignored small selection");
            }
            else
            {
                Point p1 = new Point(startMousePos.x, frameMat.height() - startMousePos.y);
                Point p2 = new Point(endMousePos.x, frameMat.height() - endMousePos.y);
                
                double x = Math.Min(p1.x, p2.x);
                double y = Math.Min(p1.y, p2.y);
                double width = Math.Abs(p2.x - p1.x);
                double height = Math.Abs(p2.y - p1.y);
                var rect = new Rect((int)x, (int)y, (int)width, (int)height);
                
                if (rect.x < 0 || rect.y < 0 || rect.x + rect.width > frameMat.cols() || rect.y + rect.height > frameMat.rows()) {
                    Debug.LogError($"Bbox out of bounds: {rect}");
                    return;
                }if (frameMat.empty()) {
                    Debug.LogError("Frame is empty!");
                }

                var tracker = TrackerCSRT.create();
                tracker.init(frameMat, rect);
                trackers.Add(tracker);
                bboxes.Add(rect);
            }
        }
        
        Utils.matToTexture2D(frameMat, texture);
        RenderTexture temp = RenderTexture.active;
        RenderTexture.active = SendTexture;
        GL.Clear(true, true, Color.clear);
        Graphics.Blit(texture, SendTexture);
        RenderTexture.active = temp;
    }
}