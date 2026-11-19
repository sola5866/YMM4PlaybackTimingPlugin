using System.Diagnostics;
using YMM4PlaybackTimingPlugin.Playback;

namespace YMM4PlaybackTimingPlugin.Composition;

// Process startup owns the native playback timing connection.
internal sealed class PlaybackTimingStartup
{
    private readonly object gate=new();private bool initialized,stopping;
    private readonly TaskCompletionSource initialization=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? completion;private PlaybackTimingRegistrationOwner? owner;
    internal string Reason {get;private set;}="not-initialized";
    internal bool IsActive {get{lock(gate)return !stopping&&owner?.IsActive==true;}}
    internal void Initialize(Func<PreviewPlaybackSession>? createSession=null,Action<int>? afterPatch=null)
    {
        lock(gate){if(initialized)return;initialized=true;}
        try
        {
            owner=new(createSession:createSession,afterPatch:afterPatch,retain:value=>{lock(gate){owner=value;if(stopping)value.CloseAsync();}});
            Reason="playback-timing-native-active";
        }
        catch(Exception error)
        {Reason="playback-timing-unavailable:"+error.GetType().Name;Trace.TraceWarning("Playback timing startup failed: {0}",error);}
        finally{initialization.TrySetResult();}
    }
    internal Task CloseAsync()
    {
        TaskCompletionSource promise;
        lock(gate)
        {
            stopping=true;owner?.CloseAsync();
            if(completion is not null)return completion;
            if(!initialized)return Task.CompletedTask;
            promise=new(TaskCreationOptions.RunContinuationsAsynchronously);completion=promise.Task;
        }
        _=Finish(promise);return promise.Task;
    }
    private async Task Finish(TaskCompletionSource promise)
    {
        try{await initialization.Task.ConfigureAwait(false);if(owner is not null)await owner.CloseAsync().ConfigureAwait(false);promise.TrySetResult();}
        catch(Exception error){promise.TrySetException(error);}
    }
}
