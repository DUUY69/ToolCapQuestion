using System;

namespace CaptureRegionApp.Processing.Models;

public static class ResultBus
{
    public static event EventHandler<AnswerResult>? ResultAdded;

    public static void Publish(AnswerResult result)
    {
        ResultAdded?.Invoke(null, result);
    }
}

