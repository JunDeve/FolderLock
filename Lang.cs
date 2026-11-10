using System.Globalization;

namespace FolderLock
{
    /// <summary>
    /// 화면 문구 한/영 전환. Windows 표시 언어가 한국어면 한글, 그 외에는 영어.
    /// 실행 인자 --lang=ko / --lang=en 으로 강제할 수 있다.
    /// </summary>
    internal static class L
    {
        public static bool Ko { get; set; } = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ko";

        /// <summary>한글/영어 문구 중 현재 언어에 맞는 것.</summary>
        public static string T(string ko, string en) => Ko ? ko : en;
    }
}
