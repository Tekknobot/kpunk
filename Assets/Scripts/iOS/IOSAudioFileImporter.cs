using System.Runtime.InteropServices;
using UnityEngine;

namespace KMusic.iOS
{
    public static class IOSAudioFileImporter
    {
#if UNITY_IOS && !UNITY_EDITOR
        [DllImport("__Internal")]
        private static extern void KMusic_OpenAudioDocumentPicker(string gameObjectName, string successCallback, string cancelCallback);
#endif

        public static void PickAudioFile(string gameObjectName, string successCallback, string cancelCallback)
        {
#if UNITY_IOS && !UNITY_EDITOR
            KMusic_OpenAudioDocumentPicker(gameObjectName, successCallback, cancelCallback);
#else
            Debug.Log("[IOSAudioFileImporter] PickAudioFile is only available on iOS device builds.");
#endif
        }
    }
}
