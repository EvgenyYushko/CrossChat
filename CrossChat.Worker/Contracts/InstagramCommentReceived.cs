namespace CrossChat.Worker.Contracts
{
    public class InstagramCommentReceived
    {
        // ID страницы (бизнес-аккаунта), которой принадлежит пост
        public string BusinessAccountId { get; set; } = string.Empty;
        public string CommentId { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        
        // НОВОЕ ПОЛЕ: ID поста, под которым написан комментарий
        public string? MediaId { get; set; }
    }
}