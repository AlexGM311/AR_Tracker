using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using JetBrains.Annotations;
using Unity.WebRTC;
using UnityEngine;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine.Networking;
using SocketIOClient;
using Cysharp.Threading.Tasks;
using UnityEngine.Experimental.Rendering;

public class WebRtcManager : MonoBehaviour
{
    private sealed class CallOfferData
    {
        public string Sdp { get; set; }
        public RTCSdpType Type { get; set; }
        public string CompanionID { get; set; }
    }

    private string token;
    private string profileId;
    [CanBeNull] private RTCPeerConnection pc;
    private string companionId;
    private SocketIO socket;
    private Texture remoteTexture;
    private static WebRtcManager _instance;
    public static Texture RemoteTexture => _instance.remoteTexture;
    public static VideoStreamTrack vst;

    
    async void Start()
    {
        _instance ??= this;
        StartCoroutine(WebRTC.Update());
        var uri = new Uri("https://dev.dinsp.ru");
        await GetToken();
        Debug.Log("Got token and pid");
        vst = new VideoStreamTrack(CameraCapture.BBoxTexture);
        
        socket = new SocketIOUnity(uri, new SocketIOOptions
        {
            EIO = EngineIO.V4,
            Transport = SocketIOClient.Transport.TransportProtocol.WebSocket,
            ExtraHeaders = new Dictionary<string, string>
            {
                { "Authorization", "Bearer " + token }
            }
        });

        socket.OnConnected += async (sender, args) =>
        {
            Debug.Log("SocketIO: подключились; " + socket.Id);
            await socket.EmitAsync("store_user", new Dictionary<string, string> { { "profileId", profileId } });
        };

        socket.OnDisconnected += (sender, args) => { Debug.Log("SocketIO: Отключились"); };

        socket.On("call_incoming", OnCallIncoming);

        socket.On("call_accept", response => { Debug.Log("received call_accept " + response); });

        socket.On("call_offer_received", OnCallOfferReceived);

        socket.On("call_ice_candidate", response =>
        {
            Debug.Log("call_ice_candidate: " + response);
            var data = JArray.Parse(response.ToString())[0];
            if (pc != null && data["candidate"] != null)
            {
                var candidateDict = data["candidate"] as JObject;

                string rawCandidate = candidateDict?["candidate"]?.ToString() ?? "";
                string sdpMid = candidateDict?["sdpMid"]?.ToString();
                int? sdpMLineIndex = candidateDict?["sdpMLineIndex"]?.ToObject<int>();

                if (rawCandidate.StartsWith("candidate:"))
                {
                    rawCandidate = rawCandidate.Substring("candidate:".Length);
                }

                try
                {
                    var iceCandidate = new RTCIceCandidate(new RTCIceCandidateInit
                    {
                        candidate = "candidate:" + rawCandidate,
                        sdpMid = sdpMid,
                        sdpMLineIndex = sdpMLineIndex
                    });

                    pc.AddIceCandidate(iceCandidate);
                    Debug.Log("Добавлен ICE-кандидат: " + iceCandidate.Candidate);
                }
                catch (Exception e)
                {
                    Debug.LogError("Ошибка при добавлении ICE-кандидата: " + e);
                }
            }
        });

        socket.On("call_hangup", response =>
        {
            Debug.Log("call_hangup: " + response);
            CloseWebRtc();
        });

        socket.On("call_reject", response =>
        {
            Debug.Log("call_reject: " + response);
            CloseWebRtc();
        });


        Debug.Log("Trying to connect to server");
        await socket.ConnectAsync();
    }
    
    private async void OnCallIncoming(SocketIOResponse response)
    {
        try
        {
            Debug.Log("call_incoming " + response);
            var data = JArray.Parse(response.ToString())[0];
            if (data["caller"] != null)
            {
                companionId = data["caller"]["id"]?.ToString();
                Debug.Log($"Получен звонок от " + companionId);
                await AcceptIncomingCall();
            }
            else
            {
                throw new ArgumentException("Couldn't parse caller data");
            }
        }
        catch (Exception e)
        {
            Debug.LogError(e);
        }
    }

    private async Task AcceptIncomingCall()
    {
        if (companionId == null)
            throw new NullReferenceException("Companion id can't be null");

        var msg = JObject.FromObject(new Dictionary<string, string> { { "companionId", companionId } });
        Debug.Log("Emitted call_accept -> " + msg);
        await socket.EmitAsync("call_accept",
            response => { Debug.Log("call_accept server responded with " + response); }, new { companionId });
    }

    private async void OnCallOfferReceived(SocketIOResponse response)
    {
        Debug.Log("call_offer_received " + response);
        try
        {
            var ret = JArray.Parse(response.ToString())[0];
            companionId = ret["callerId"]?.ToString();
            Debug.Log($"SDP string is " + ret["sdp"]?["sdp"]);
            if (pc != null)
            {
                Debug.Log("Warning: pc already exists. Destroying and recreating.");
                pc.Close();
                pc.Dispose();
            }
            
            SetupWebRtc();
            
            if (!Enum.TryParse<RTCSdpType>(ret["sdp"]?["type"]?.ToString(), true, out var type))
            {
                throw new ArgumentException("Couldn't parse sdp type from " + ret["sdp"]?["type"]?.ToString());
            }
            var offer = new RTCSessionDescription { sdp = ret["sdp"]?["sdp"]?.ToString(), type = type };
            await pc.SetRemoteDescription(ref offer);
            foreach (var transceiver in pc.GetTransceivers())
            {
                Debug.Log($"Transceiver: {transceiver.Mid} direction={transceiver.Direction} sender={transceiver.Sender?.Track?.Kind} receiver={transceiver.Receiver?.Track?.Kind}");
            }
            var answer = pc.CreateAnswer();
            await answer;
            if (!answer.IsDone)
                Debug.LogError("Операция не завершилась!");
            if (answer.IsError)
                Debug.LogError(answer.Error);
            var description = answer.Desc;
            await pc.SetLocalDescription(ref description);

            var msg = new
            {
                companionId = companionId,
                sdp = new
                {
                    type = description.type.ToString().ToLower(),
                    sdp = description.sdp
                }
            };

            Debug.Log("call_create_answer -> " + msg);
            await socket.EmitAsync("call_create_answer", msg);
        }
        catch (Exception e)
        {
            Debug.LogError(e);
        }
    }

    private async Task GetToken()
    {
        var json = new Dictionary<string, string> { { "username", "cv.expert" }, { "password", "12345678" } };
        var request = new UnityWebRequest("https://dev.dinsp.ru/api/v1/auth/signIn", "POST");
        byte[] bodyRaw = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(json));
        request.uploadHandler = new UploadHandlerRaw(bodyRaw);
        request.downloadHandler = new DownloadHandlerBuffer();
        request.SetRequestHeader("Content-Type", "application/json");

        await request.SendWebRequest();
        if (request.result == UnityWebRequest.Result.Success)
        {
            string receivedJson = request.downloadHandler.text;
            var jObject = JObject.Parse(receivedJson);
            token = jObject["data"]?["accessToken"]?.ToString();
            profileId = jObject["data"]?["profile"]?["id"]?.ToString();

            if (token == null || profileId == null)
                throw new NullReferenceException("Token or profile id cannot be null");
            Debug.Log("Got token and profile: \n" + token + "\n" + profileId);
        }
        else
            Debug.LogError("Auth failed: " + request.error);
    }

    void SetupWebRtc()
    {
        Debug.Log("Settings up WebRTC connection");
        var configuration = new RTCConfiguration
        {
            iceServers = new[]
            {
                new RTCIceServer
                {
                    urls = new[] { "turn:turn.dinsp.ru:80" }, username = "user", credential = "dev12345",
                    credentialType = RTCIceCredentialType.Password
                },
                new RTCIceServer
                {
                    urls = new[] { "turns:turn.dinsp.ru:443" }, username = "user", credential = "dev12345",
                    credentialType = RTCIceCredentialType.Password
                },
                new RTCIceServer { urls = new[] { "stun:stun.dinsp.ru:80" } },
                new RTCIceServer { urls = new[] { "stun:stun.l.google.com:19302" } },
            }
        };
        pc = new RTCPeerConnection(ref configuration);
        pc.AddTrack(vst);
        pc.OnIceCandidate += OnOnIceCandidate;
        pc.OnTrack += e =>
        {
            Debug.Log($"OnTrack: kind={e.Track.Kind} mid={e.Transceiver.Mid} trackId={e.Track.Id}");

            if (e.Track is VideoStreamTrack track)
            {
                track.OnVideoReceived += tex =>
                {
                    Debug.Log($"[Video] Resolution changed to {tex.width}x{tex.height}");
                    remoteTexture = tex;
                };
                Debug.Log($"track.Enabled = {track.Enabled}, ReadyState = {track.ReadyState}");
            }
            else
            {
                Debug.Log("Аудиопоток игнорируем");
            }
        };
        pc.OnConnectionStateChange += state =>
        {
            if (pc == null)
                remoteTexture = null;                
            Debug.Log("Состояние соединения: " + state);
            if (pc.ConnectionState == RTCPeerConnectionState.Connected)
                Debug.Log("pc подключен!");
            if (pc.ConnectionState is RTCPeerConnectionState.Closed or RTCPeerConnectionState.Disconnected
                or RTCPeerConnectionState.Failed)
            {
                Debug.Log("Закрытие pc");
                CloseWebRtc();
            }
        };
    }

    private void CloseWebRtc()
    {
        if (pc != null)
        {
            Debug.Log("Закрываем pc");
            pc.Close();
            pc = null;
        }

        companionId = null;
    }

    private async void OnOnIceCandidate(RTCIceCandidate candidate)
    {
        if (candidate != null && companionId != null)
        {
            var msg = JObject.FromObject(new
            {
                companionId = companionId,
                candidate = new
                {
                    candidate = candidate.Candidate,
                    sdpMid = candidate.SdpMid,
                    sdpMLineIndex = candidate.SdpMLineIndex
                }
            });
            Debug.Log("Отправляем call_ice_candidate" + msg);
            await socket.EmitAsync("call_ice_candidate", msg);
        }
        else
        {
            Debug.LogWarning($"candidate {candidate}; companionId {companionId}");
        }
    }

    private static async void Hangup()
    {
        await _instance.socket.EmitAsync("call_hangup", new {_instance.companionId});
        _instance.CloseWebRtc();
    }
    
    public void OnDestroy()
    {
        Hangup();
    }

    public static void FixTrack()
    {
        if (vst is { } unityTrack)
        {
            // For Unity WebRTC package
            Debug.Log($"Is video track alive? {unityTrack.ReadyState == TrackState.Live}");
        }
        
        vst = new VideoStreamTrack(CameraCapture.BBoxTexture);
        _instance.pc?.AddTrack(vst);
    }
}