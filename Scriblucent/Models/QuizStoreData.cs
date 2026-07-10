namespace Scriblucent.Models;

public sealed class QuizStoreData
{
    public List<QuizDocument> Quizzes { get; set; } = new();
    public List<QuizGroupData> Groups { get; set; } = new();
}
