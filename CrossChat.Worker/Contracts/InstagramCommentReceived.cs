namespace CrossChat.Worker.Contracts;

public record InstagramCommentReceived
{
    // ID страницы (бизнес-аккаунта), которой принадлежит пост
    public string BusinessAccountId { get; set; } = string.Empty; 
    
    // Уникальный ID самого комментария (нужен для ответа)
    public string CommentId { get; set; } = string.Empty;
    
    // Текст комментария
    public string Text { get; set; } = string.Empty;
    
    // Кто написал (username)
    public string Username { get; set; } = string.Empty;
    
    // Является ли это ответом на другой коммент (опционально)
    public string? ParentId { get; set; }
}