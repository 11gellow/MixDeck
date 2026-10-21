using AudioManager.Core.Models;

namespace AudioManager.Core.Events;

/// <summary>新增会话的创建时快照；元数据读取不完整时保留 Issues。</summary>
public sealed record AudioSessionCreated(AudioSession Session, IReadOnlyList<AudioReadIssue> Issues);
