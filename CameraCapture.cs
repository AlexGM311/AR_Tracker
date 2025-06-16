using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using OpenCVForUnity.CoreModule;
using OpenCVForUnity.ImgprocModule;
using OpenCVForUnity.TrackingModule;
using OpenCVForUnity.UnityUtils;
using OpenCVForUnity.VideoModule;
using Unity.WebRTC;
using UnityEngine.Android;
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
    private bool isTracking = false;
    private List<Tracker> trackers = new List<Tracker>();
    private bool buttonDown = false;
    private bool buttonPressed = false;
    private List<Rect> bboxes = new List<Rect>();
    public static RenderTexture BBoxTexture;
    public static Mat BboxMat;
    private Texture2D testImage;
    private RenderTexture videoBuffer;
    private bool isTextureChanging = false;
    private bool fixVst = false;

    public void Awake()
    {
        testImage = Resources.Load<Texture2D>("7 1");
        BBoxTexture = new RenderTexture(640, 360, 0, GraphicsFormat.B8G8R8A8_SRGB);
        BBoxTexture.Create();
    }

    public void OnSelectButtonClicked()
    {
        buttonDown = true;
        buttonPressed = true;
        isTracking = false;
        trackers.Clear();
        bboxes.Clear();
        Debug.Log("Начат процесс выбора");
    }
    
    IEnumerator HandleTextureResize()
    {
        isTextureChanging = true;
    
        // Dispose old resources
        frameMat?.Dispose();
        Destroy(texture);
        Destroy(BBoxTexture);
    
        // Wait until end of frame to prevent GL errors
        yield return new WaitForEndOfFrame();
    
        // Create new resources
        try 
        {
            frameMat = new Mat(
                WebRtcManager.RemoteTexture.height, 
                WebRtcManager.RemoteTexture.width, 
                CvType.CV_8UC4);
            
            texture = new Texture2D(
                WebRtcManager.RemoteTexture.width, 
                WebRtcManager.RemoteTexture.height, 
                GraphicsFormat.B8G8R8A8_SRGB,
                TextureCreationFlags.None);
            
            BBoxTexture = new RenderTexture(
                640, 
                360,
                GraphicsFormat.B8G8R8A8_SRGB,
                0);
            fixVst = true;
        }
        catch (Exception e)
        {
            Debug.LogError($"Resize failed: {e.Message}");
        }
    
        isTextureChanging = false;
    }
    
    void Update()
    {
        if (isTextureChanging) return;
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
        if (frameMat == null || !texture || texture.width != WebRtcManager.RemoteTexture.width 
            || texture.height != WebRtcManager.RemoteTexture.height)
        {
            StartCoroutine(HandleTextureResize());
            return;
        }
        if (fixVst)
        {
            WebRtcManager.FixTrack();
            fixVst = false;
        }
        
        
        BboxMat = new Mat(new Size(640, 360), CvType.CV_8UC4, new Scalar(0, 0, 0, 0));
        if (!BBoxTexture || BBoxTexture.width != BboxMat.width())
        {
            Destroy(BBoxTexture);
            BBoxTexture = new RenderTexture(
                BboxMat.width(), 
                BboxMat.height(), 
                0, 
                GraphicsFormat.B8G8R8A8_SRGB
            );
        }
        
        Utils.textureToTexture2D(WebRtcManager.RemoteTexture, texture);
        Utils.texture2DToMat(texture, frameMat);
        
        if (Input.GetKey(KeyCode.B))
        {
            if (BBoxTexture)
            {
                display.texture = BBoxTexture;
                display.rectTransform.sizeDelta = new Vector2(
                    BBoxTexture.width,
                    BBoxTexture.height
                );
            }
            else
            {
                Debug.LogWarning("BBoxTexture is null");
            }
        }
        else
        {
            display.texture = texture;
        }

        if (!display.texture)
            display.texture = texture;
        // ======== Трекинг ========
        if (isTracking)
        {
            for (int i = 0; i < trackers.Count; i++)
            {
                Rect updatedBox = new Rect();
                bool ok = trackers[i].update(frameMat, updatedBox);
                if (ok)
                {
                    Debug.Log("Drawing boxes");
                    bboxes[i] = updatedBox;
                    Imgproc.rectangle(frameMat, updatedBox.tl(), updatedBox.br(), new Scalar(255, 0, 0, 255), 2);
                    Imgproc.rectangle(BboxMat, updatedBox.tl(), updatedBox.br(), new Scalar(255, 0, 0, 255), 2);
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

            float scaleX = (float)WebRtcManager.RemoteTexture.width / rt.rect.width;
            float scaleY = (float)WebRtcManager.RemoteTexture.height / rt.rect.height;

            float camX = x * scaleX;
            float camY = y * scaleY;

            mousePos = new Vector2(camX, camY); // Теперь mousePos точно в пикселях frameMat
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
            Imgproc.rectangle(BboxMat, p1, p2, new Scalar(0, 255, 0, 255), 2);
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
                bboxes.Add(new Rect(p1, p2));
                Debug.Log($"Added bb {p1} {p2}");

                var tracker = TrackerCSRT.create();
                tracker.init(frameMat, bboxes[^1]);
                trackers.Add(tracker);
            }
        }
        if (bboxes.Count > 0 && !isTracking)
        {
            Debug.Log($"{bboxes.Count} - bounding boxes count");
            if (bboxes.Count > 1)
            {
                bboxes = new List<Rect>{bboxes.Last()};
            }
            StartTracking();
        }
        
        Texture2D tempTex = new Texture2D(BboxMat.width(), BboxMat.height(), GraphicsFormat.B8G8R8A8_SRGB, TextureCreationFlags.None);
        Utils.matToTexture2D(frameMat, texture);
        Utils.matToTexture2D(BboxMat, tempTex);
        Graphics.Blit(tempTex, BBoxTexture);
        // Graphics.Blit(testImage, BBoxTexture);
        BboxMat.Dispose();
        Destroy(tempTex);
    }   

    void StartTracking()
    {
        isTracking = true;
        trackers.Clear();               

        for (int i = 0; i < bboxes.Count; i++)
        {
            var tracker = TrackerCSRT.create();
            tracker.init(frameMat, bboxes[i]);
            trackers.Add(tracker);
        }
    }
}