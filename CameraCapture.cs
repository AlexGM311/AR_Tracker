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
using UnityEngine.Android;
using UnityEngine.UI;
using Rect = OpenCVForUnity.CoreModule.Rect;

public class CameraCapture : MonoBehaviour
{
    public RawImage display;

    WebCamTexture webCamTexture;
    Mat frameMat;
    Texture2D texture;

    private Vector2 startMousePos;
    private Vector2 endMousePos;
    private bool isSelecting = false;
    private Rect2d trackingRect;
    private bool isTracking = false;
    private List<Tracker> trackers = new List<Tracker>();
    private bool isDragging = false;
    private bool buttonDown = false;
    private bool buttonPressed = false;
    private List<Rect> bboxes = new List<Rect>();
    
    public void OnSelectButtonClicked()
    {
        buttonDown = true;
        buttonPressed = true;
        isTracking = false;
        trackers.Clear();
        bboxes.Clear();
        Debug.Log("Начат процесс выбора");
    }

    
    void Start()
    {
    #if UNITY_ANDROID && !UNITY_EDITOR
        if (!Permission.HasUserAuthorizedPermission(Permission.Camera)) {
            Permission.RequestUserPermission(Permission.Camera);
        }
    #endif
        
        StartCoroutine(InitializeCamera());
    }
    
    IEnumerator InitializeCamera() {
        // Wait until permissions are granted (Android)
        while (!Permission.HasUserAuthorizedPermission(Permission.Camera)) {
            yield return null;
        }

        // Check if any camera is available
        if (WebCamTexture.devices.Length == 0) {
            Debug.LogError("No camera found!");
            yield break;
        }

        WebCamDevice device = WebCamTexture.devices[0];

        yield return InitializeCameraAtFraction(device, 2);
    }

    IEnumerator InitializeCameraAtFraction(WebCamDevice device, float fraction)
    {
        webCamTexture = new WebCamTexture(device.name, (int)(Screen.width / fraction), (int)(Screen.height / fraction), 60);
        webCamTexture.Play();
        yield return new WaitUntil(() => webCamTexture.width > 16);

        // Только теперь создаём матрицу и текстуру
        frameMat = new Mat(webCamTexture.height, webCamTexture.width, CvType.CV_8UC3);
        texture = new Texture2D(webCamTexture.width, webCamTexture.height, TextureFormat.RGB24, false);
        display.texture = texture;
        AdjustAspectRatio();
    } 

    void AdjustAspectRatio()
    {
        float textureAspect = (float)webCamTexture.width / webCamTexture.height;
        float screenAspect = (float)Screen.width / Screen.height;

        RectTransform rt = display.rectTransform;

        if (textureAspect > screenAspect)
        {
            // Камера шире — ограничиваем по ширине
            float width = Screen.width;
            float height = width / textureAspect;
            rt.sizeDelta = new Vector2(width, height);
        }
        else
        {
            // Камера выше — ограничиваем по высоте
            float height = Screen.height;
            float width = height * textureAspect;
            rt.sizeDelta = new Vector2(width, height);
        }
    }
    
    void Update()
    {
        if (!webCamTexture.isPlaying)
            return;
        
        Utils.texture2DToMat(WebRTCReceiver.remoteTexture, frameMat);
        Imgproc.cvtColor(frameMat, frameMat, Imgproc.COLOR_RGBA2RGB);
        
        // ======== Трекинг ========
        if (isTracking)
        {
            Debug.Log($"Tracking {trackers.Count} boxes");
            for (int i = 0; i < trackers.Count; i++)
            {
                Rect updatedBox = new Rect();
                bool ok = trackers[i].update(frameMat, updatedBox);
                if (ok)
                {
                    Debug.Log("drawing");
                    bboxes[i] = updatedBox;
                    Imgproc.rectangle(frameMat, updatedBox.tl(), updatedBox.br(), new Scalar(255, 0, 0), 2);
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

            float scaleX = (float)webCamTexture.width / rt.rect.width;
            float scaleY = (float)webCamTexture.height / rt.rect.height;

            float camX = x * scaleX;
            float camY = y * scaleY;

            mousePos = new Vector2(camX, camY); // Теперь mousePos точно в пикселях frameMat
        }
        else
        {
            mousePos = new Vector2(-1e9f, -1e9f); // вне RawImage — пропускаем кадр
        }
        if (buttonDown && !pressed) buttonDown = false;
        else if (buttonDown || !buttonPressed) {Debug.Log("Quitting early");} // Если прошлый if не сработал, а кнопка нажата
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
            Imgproc.rectangle(frameMat, p1, p2, new Scalar(0, 255, 0), 2);
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
        
        Utils.fastMatToTexture2D(frameMat, texture);
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


    void OnDestroy()
    {
        if (webCamTexture != null)
        {
            webCamTexture.Stop();
        }
    }
}
