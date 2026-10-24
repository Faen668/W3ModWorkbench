using NAudio.Wave;

namespace W3ModWorkbench;

/// <summary>Starts the app's sound playing: silent when it runs <see cref="App.Headless"/> for a test program, still in time.</summary>
public static class SoundOut
{
    public static void Play(WaveOutEvent output)
    {
        if (App.Headless) output.Volume = 0;
        output.Play();
    }
}
