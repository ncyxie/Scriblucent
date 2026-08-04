namespace Scriblucent.Models;

/// <summary>Per-set choice of spaced-repetition engine. Switching a set's algorithm resets its
/// scheduling progress (see FlashcardsPreviewWindow's reset-on-switch flow) - there's no lossy
/// conversion between SM-2's ease/interval state and FSRS's difficulty/stability state.</summary>
public enum SpacedRepetitionAlgorithm
{
    Sm2,
    Fsrs6
}
