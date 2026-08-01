import test from 'node:test';
import assert from 'node:assert/strict';
import { normalizeSkillDetail } from '../src/bridge/skillDetailNormalizer.js';

test('旧宿主字段归一化后不保留 PascalCase 别名', () => {
  const result = normalizeSkillDetail({
    success: true,
    Skill: { Name: 'xlsx' },
    Content: '# xlsx',
    Resources: [{ RelativePath: 'references/example.md', Kind: 'references', SizeBytes: 42, IsText: true }],
    Scripts: [{ SkillName: 'xlsx', RelativePath: 'scripts/recalc.py', Runtime: 'python', SizeBytes: 128, Sha256: 'abc', IsApproved: false }],
    traceId: 'trace-1'
  });

  assert.deepEqual(result.skill, { Name: 'xlsx' });
  assert.equal(result.content, '# xlsx');
  assert.deepEqual(result.resources, [
    { relativePath: 'references/example.md', kind: 'references', sizeBytes: 42, isText: true }
  ]);
  assert.deepEqual(result.scripts, [
    {
      skillName: 'xlsx',
      relativePath: 'scripts/recalc.py',
      runtime: 'python',
      sizeBytes: 128,
      sha256: 'abc',
      isApproved: false
    }
  ]);
  assert.equal(result.Skill, undefined);
  assert.equal(result.Content, undefined);
  assert.equal(result.Resources, undefined);
  assert.equal(result.Scripts, undefined);
  assert.equal(result.traceId, 'trace-1');
});

test('同时存在两种命名时 camelCase 字段优先', () => {
  const result = normalizeSkillDetail({
    skill: { name: 'current' },
    Skill: { name: 'legacy' },
    content: 'current content',
    Content: 'legacy content',
    resources: [{ relativePath: 'current.md' }],
    Resources: [{ RelativePath: 'legacy.md' }],
    scripts: [{ relativePath: 'scripts/current.py', runtime: 'python' }],
    Scripts: [{ RelativePath: 'scripts/legacy.py', Runtime: 'python' }]
  });

  assert.deepEqual(result.skill, { name: 'current' });
  assert.equal(result.content, 'current content');
  assert.equal(result.resources[0].relativePath, 'current.md');
  assert.equal(result.scripts[0].relativePath, 'scripts/current.py');
  assert.equal(result.Skill, undefined);
  assert.equal(result.Content, undefined);
  assert.equal(result.Resources, undefined);
  assert.equal(result.Scripts, undefined);
});
