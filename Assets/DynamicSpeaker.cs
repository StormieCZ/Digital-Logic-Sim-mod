using UnityEngine;
using DLS.Simulation; // Make sure you can access the Simulator's namespace

[RequireComponent(typeof(AudioSource))]
public class DynamicSpeaker : MonoBehaviour
{
    [Tooltip("Match this ID to the chip's InternalState[0] in the simulator")]
    public uint speakerID = 0;

    [Range(0.0f, 1.0f)]
    public float gain = 0.1f; // Volume

    private float currentFrequency = 0.0f;
    private float phase = 0.0f;
    private int samplingRate;

    void Awake()
    {
        // Get the audio system's sample rate
        samplingRate = AudioSettings.outputSampleRate;
    }

    // This magic function is called by Unity to get audio samples
    void OnAudioFilterRead(float[] data, int channels)
    {
        // 1. Try to get the frequency from the simulator's buffer
        // If the key doesn't exist, it will default to 0.0f
        Simulator.SpeakerFrequencyBuffer.TryGetValue(speakerID, out currentFrequency);


        // Debug
        //if (currentFrequency > 0) Debug.Log("Playing frequency: " + currentFrequency);


        // 2. Calculate how much to increment the phase for each sample
        float phaseIncrement = (2.0f * Mathf.PI * currentFrequency) / samplingRate;

        // 3. Fill the data buffer with a sine wave
        for (int i = 0; i < data.Length; i += channels)
        {
            phase += phaseIncrement;

            // Generate the sine wave sample
            float sample = gain * Mathf.Sin(phase);

            // Write the sample to all channels (mono, stereo, etc.)
            for (int c = 0; c < channels; c++)
            {
                data[i + c] = sample;
            }

            // Keep phase from getting too large
            if (phase > (2.0f * Mathf.PI))
            {
                phase -= (2.0f * Mathf.PI);
            }
        }
    }
}