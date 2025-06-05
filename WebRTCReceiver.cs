using UnityEngine;
using Unity.WebRTC;
using UnityEngine.Networking;
using System.Collections;
using System.Text;

public class WebRTCReceiver : MonoBehaviour
{
    private RTCPeerConnection _peerConnection;
    private VideoStreamTrack _remoteTrack;
    public static Texture2D remoteTexture;

    [System.Serializable]
    public class RTCSessionDescriptionPayload
    {
        public string sdp;
        public string type;
    }
    
    void Start()
    {
        StartCoroutine(StartConnection());
    }

    IEnumerator StartConnection()
    {
        var config = GetConfiguration();
        _peerConnection = new RTCPeerConnection(ref config);

        _peerConnection.OnTrack = e =>
        {
            Debug.Log("Got remote track");
            if (e.Track is VideoStreamTrack videoTrack)
            {
                _remoteTrack = videoTrack;
                remoteTexture = new Texture2D(videoTrack.Texture.width, videoTrack.Texture.height, TextureFormat.RGBA32, false);
                videoTrack.OnVideoReceived += tex =>
                {
                    Graphics.CopyTexture(tex, remoteTexture);
                };
            }
        };

        _peerConnection.OnDataChannel = channel =>
        {
            channel.OnMessage = bytes =>
            {
                string msg = Encoding.UTF8.GetString(bytes);
                Debug.Log("Received result from device: " + msg);
            };
        };

        var offerOp = _peerConnection.CreateOffer();
        yield return offerOp;
        var offer = offerOp.Desc;
        yield return _peerConnection.SetLocalDescription(ref offer);

        // Send offer to device
        yield return StartCoroutine(SendOfferToPythonServer(offer));
    }

    RTCConfiguration GetConfiguration()
    {
        return new RTCConfiguration
        {
            iceServers = new[] {
                new RTCIceServer { urls = new[] { "stun:stun.l.google.com:19302" } }
            }
        };
    }

    IEnumerator SendOfferToPythonServer(RTCSessionDescription offer)
    {
        var payload = new RTCSessionDescriptionPayload
        {
            sdp = offer.sdp,
            type = offer.type.ToString().ToLower()
        };

        string jsonString = JsonUtility.ToJson(payload);
        var url = "http://192.168.157.207:8080/offer";

        var request = new UnityWebRequest(url, "POST");
        byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonString);
        request.uploadHandler = new UploadHandlerRaw(bodyRaw);
        request.downloadHandler = new DownloadHandlerBuffer();
        request.SetRequestHeader("Content-Type", "application/json");

        yield return request.SendWebRequest();

        if (request.result == UnityWebRequest.Result.Success)
        {
            string responseText = request.downloadHandler.text;
            var answerPayload = JsonUtility.FromJson<RTCSessionDescriptionPayload>(responseText);
            var answer = new RTCSessionDescription
            {
                type = RTCSdpType.Answer,
                sdp = answerPayload.sdp
            };
            yield return _peerConnection.SetRemoteDescription(ref answer);
        }
        else
        {
            Debug.LogError("Signaling failed: " + request.error);
        }
    }

    private void OnDestroy()
    {
        _peerConnection?.Close();
        _remoteTrack?.Dispose();
    }
}
