namespace LivingValleyOpenRouter
{
    /// <summary>config.json 설정 클래스</summary>
    public class ModConfig
    {
        /// <summary>OpenRouter API 키</summary>
        public string OpenRouterApiKey { get; set; } = "여기에_OpenRouter_API_키를_입력하세요";

        /// <summary>사용할 AI 모델</summary>
        public string Model { get; set; } = "google/gemini-2.5-flash-preview";

        /// <summary>최대 응답 토큰 수</summary>
        public int MaxTokens { get; set; } = 500;

        /// <summary>Transcript Archive 활성화 여부</summary>
        public bool EnableTranscriptArchive { get; set; } = true;

        /// <summary>응답 온도 (높을수록 창의적)</summary>
        public float Temperature { get; set; } = 0.8f;

        /// <summary>디버그 로그 출력 여부</summary>
        public bool Debug { get; set; } = true;

        /// <summary>히스토리 토큰 한도 — 초과 시 요약 발동 (추정치 기반)</summary>
        public int HistoryTokenLimit { get; set; } = 8000;

        /// <summary>요약 발동 시 보존할 최근 턴 수</summary>
        public int RecentTurnsToKeep { get; set; } = 6;

        /// <summary>요약용 모델 (비워두면 메인 모델 사용)</summary>
        public string SummaryModel { get; set; } = "";

        /// <summary>전체 요청 토큰 상한 (이 이상이면 오래된 히스토리 강제 삭제)</summary>
        public int MaxTotalTokens { get; set; } = 45000;
    }
}