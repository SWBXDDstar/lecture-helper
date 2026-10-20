using System;

namespace LectureHelper.Native;

/// <summary>
/// 전체 화면 오버레이는 시작할 때 포커스를 가져간다. 닫힐 때 원래 앞에 있던 창(Chrome 등)에
/// 돌려주지 않으면 윈도우가 갈 곳을 못 찾아 바탕화면이 나온다. 오버레이를 만들 때 하나 만들어 두고,
/// 닫히기 시작할 때(아직 화면을 덮고 있을 때) <see cref="Restore"/> 를 부른다.
/// 사장님이 일부러 최소화한 창은 다시 펴지 않는다.
/// </summary>
public sealed class ForegroundRestorer
{
    private readonly IntPtr _previous = NativeMethods.GetForegroundWindow();

    public void Restore()
    {
        if (_previous == IntPtr.Zero || !NativeMethods.IsWindow(_previous)) return;
        if (NativeMethods.IsIconic(_previous)) return;

        NativeMethods.SetForegroundWindow(_previous);
    }
}
