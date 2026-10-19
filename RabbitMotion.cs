namespace TokenPet;

public enum RabbitAction { Idle, Walk, Jump, Wave, Sleep, Prone, Drag }

public readonly record struct RabbitFrame(string Asset, double X, double Y, double ScaleX, double ScaleY, double Angle, double HeadOffset = 0);

public static class RabbitMotion
{
    public static string Label(RabbitAction action) => action switch
    {
        RabbitAction.Walk => "산책", RabbitAction.Jump => "우라!", RabbitAction.Wave => "야하!",
        RabbitAction.Sleep => "눈 뜨고 쉬기", RabbitAction.Prone => "엎드리기", RabbitAction.Drag => "이동 중", _ => "대기"
    };

    public static RabbitFrame Sample(RabbitAction action, double seconds)
    {
        var breathe = Math.Sin(seconds * 2.2);
        return action switch
        {
            RabbitAction.Walk => new("walk", 44 * Math.Sin(seconds * 0.9), -6 * Math.Abs(Math.Sin(seconds * 9)),
                Math.Cos(seconds * 0.9) < 0 ? -1 : 1, 1 + 0.025 * Math.Sin(seconds * 18), 4 * Math.Sin(seconds * 9)),
            RabbitAction.Jump => new("jump", 0, -36 * Math.Abs(Math.Sin(seconds * Math.PI / 0.95)),
                1 + 0.035 * Math.Cos(seconds * Math.PI * 2 / 0.95), 1 - 0.035 * Math.Cos(seconds * Math.PI * 2 / 0.95), 3 * Math.Sin(seconds * 4)),
            RabbitAction.Wave => new("wave", 0, -2 * (1 + breathe), 1, 1, 7 * Math.Sin(seconds * 5)),
            RabbitAction.Sleep => new("sleep", 0, 2, 1 - breathe * 0.009, 0.97 + breathe * 0.018, -5),
            RabbitAction.Prone => new("prone", 0, 2, 1 - breathe * 0.006, 1 + breathe * 0.01, 0, 32),
            RabbitAction.Drag => new("jump", 0, -6, 0.97, 1.04, 8 * Math.Sin(seconds * 7)),
            _ => new("idle", 0, -1.5 * (1 + breathe), 1 - breathe * 0.009, 1 + breathe * 0.018, 1.5 * Math.Sin(seconds * 1.3))
        };
    }

    public static void SelfTest()
    {
        var assets = new[] { "idle", "walk", "jump", "wave", "sleep", "prone" };
        foreach (var action in Enum.GetValues<RabbitAction>())
        {
            for (var i = 0; i < 500; i++)
            {
                var frame = Sample(action, i / 30.0);
                if (!assets.Contains(frame.Asset) || !double.IsFinite(frame.X + frame.Y + frame.Angle + frame.ScaleX + frame.ScaleY) ||
                    Math.Abs(frame.X) > 44 || frame.Y < -36 || frame.Y > 3 || Math.Abs(frame.ScaleX) < 0.9 ||
                    Math.Abs(frame.ScaleX) > 1.1 || frame.ScaleY < 0.9 || frame.ScaleY > 1.1 || Math.Abs(frame.Angle) > 8 ||
                    !double.IsFinite(frame.HeadOffset) || frame.HeadOffset < 0 || frame.HeadOffset > 32)
                    throw new Exception($"Rabbit animation out of bounds: {action} at {i}");
            }
        }
        if (Sample(RabbitAction.Walk, 0).ScaleX != 1 || Sample(RabbitAction.Walk, 4).ScaleX != -1 ||
            Sample(RabbitAction.Jump, 0.475).Y > -35 || Sample(RabbitAction.Sleep, 0).Asset != "sleep" ||
            Sample(RabbitAction.Prone, 0).Asset != "prone" || Sample(RabbitAction.Prone, 0).HeadOffset != 32)
            throw new Exception("Rabbit motion direction, jump or rest is incorrect.");
    }
}
