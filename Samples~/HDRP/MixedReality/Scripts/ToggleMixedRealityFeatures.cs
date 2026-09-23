using UnityEngine;

public class ToggleMixedRealityFeatures : MonoBehaviour
{
    public MixedRealityExample mrExample;

    [Header("MR feature toggle keys")]
    public KeyCode MRToggleKey = KeyCode.Alpha1;
    public KeyCode depthEstimationToggleKey = KeyCode.Alpha2;
    public KeyCode reflectionToggleKey = KeyCode.Alpha3;
    public KeyCode VREyeOffsetToggleKey = KeyCode.Alpha4;
    public KeyCode DirectionalLightToggleKey = KeyCode.Alpha5;
    public KeyCode ChromaKeyingToggleKey = KeyCode.Alpha6;
    public KeyCode LayersToggleKey = KeyCode.Alpha7;
    public GameObject directionalLight;



    void Start()
    {
        if (!mrExample)
        {
            enabled = false;
        }
    }


    void Update()
    {
        if (Input.anyKeyDown)
        {
            if (Input.GetKeyDown(MRToggleKey))
            {
                mrExample.videoSeeThrough = !mrExample.videoSeeThrough;
            }
            if (Input.GetKeyDown(depthEstimationToggleKey))
            {
                mrExample.depthEstimation = !mrExample.depthEstimation;
            }
            if (Input.GetKeyDown(reflectionToggleKey))
            {
                mrExample.environmentReflections = !mrExample.environmentReflections;
            }
            if (Input.GetKeyDown(VREyeOffsetToggleKey))
            {
                if (mrExample.VREyeOffset == 0f)
                {
                    mrExample.VREyeOffset = 1.0f;
                }
                else
                {
                    mrExample.VREyeOffset = 0f;
                }
            }

            if (Input.GetKeyDown(DirectionalLightToggleKey))
            {
                directionalLight.SetActive(!directionalLight.activeSelf);
            }
            if (Input.GetKeyDown(ChromaKeyingToggleKey))
            {
                mrExample.chromaKeying = !mrExample.chromaKeying;
            }
            if (Input.GetKeyDown(LayersToggleKey))
            {
                mrExample.useLayers = !mrExample.useLayers;
            }
        }
    }
}