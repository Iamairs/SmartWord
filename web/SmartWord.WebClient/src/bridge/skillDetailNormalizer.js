function normalizeSkillResource(resource) {
  const source = resource || {};
  return {
    relativePath: String(source.relativePath ?? source.RelativePath ?? ''),
    kind: String(source.kind ?? source.Kind ?? ''),
    sizeBytes: Number(source.sizeBytes ?? source.SizeBytes ?? 0),
    isText: Boolean(source.isText ?? source.IsText)
  };
}

function normalizeSkillScript(script) {
  const source = script || {};
  return {
    skillName: String(source.skillName ?? source.SkillName ?? ''),
    relativePath: String(source.relativePath ?? source.RelativePath ?? ''),
    runtime: String(source.runtime ?? source.Runtime ?? ''),
    sizeBytes: Number(source.sizeBytes ?? source.SizeBytes ?? 0),
    sha256: String(source.sha256 ?? source.Sha256 ?? ''),
    isApproved: Boolean(source.isApproved ?? source.IsApproved)
  };
}

/**
 * 将旧宿主的 PascalCase Skill 详情响应收敛为唯一的 camelCase 契约。
 * 未知字段作为扩展元数据保留，但不保留已知的旧字段别名。
 */
export function normalizeSkillDetail(detail) {
  const source = detail && typeof detail === 'object' ? detail : {};
  const { Skill, Content, Resources, Scripts, ...extensions } = source;

  return {
    ...extensions,
    skill: source.skill ?? Skill ?? null,
    content: String(source.content ?? Content ?? ''),
    resources: (source.resources ?? Resources ?? []).map(normalizeSkillResource),
    scripts: (source.scripts ?? Scripts ?? []).map(normalizeSkillScript)
  };
}
