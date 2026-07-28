import { beforeEach, describe, expect, it } from 'vitest';

import { buildFixtures } from '@/api';
import { resolveActiveProject } from '@/features/shared/hooks/use-active-project';
import { useActiveProjectStore } from '@/stores/active-project-store';
import {
  isConversationSelectionCurrent,
  LAST_CONVERSATION_VALIDITY_MS,
  useConversationPreferencesStore,
} from '@/stores/conversation-preferences-store';

const fixtures = buildFixtures(42);
const [profileA, profileB] = fixtures.data.profiles;
const [projectA, projectB] = fixtures.data.projects;

describe('preferências globais por usuário', () => {
  beforeEach(() => {
    useActiveProjectStore.setState({ selectionsByProfile: {} });
    useConversationPreferencesStore.setState({ selectionsByProfileAndProject: {} });
  });

  it('isola a seleção de projeto por perfil e persiste o instante', () => {
    const selectedAt = '2026-07-28T12:00:00.000Z';
    useActiveProjectStore.getState().selectProject(profileA.id, projectA.id, selectedAt);
    useActiveProjectStore.getState().selectProject(profileB.id, projectB.id, selectedAt);

    expect(useActiveProjectStore.getState().selectionsByProfile).toEqual({
      [profileA.id]: { projectId: projectA.id, selectedAt },
      [profileB.id]: { projectId: projectB.id, selectedAt },
    });
  });

  it('não troca silenciosamente uma seleção que deixou de ser autorizada', () => {
    const unavailable = resolveActiveProject(
      [projectB],
      { projectId: projectA.id, selectedAt: '2026-07-28T12:00:00.000Z' },
      true,
    );
    expect(unavailable).toEqual({
      activeProject: null,
      selectionUnavailable: true,
    });

    const firstSelection = resolveActiveProject([projectB], undefined, true);
    expect(firstSelection.activeProject?.id).toBe(projectB.id);
    expect(firstSelection.selectionUnavailable).toBe(false);
  });

  it('preserva a última conversa por perfil e projeto com validade explícita', () => {
    const selectedAt = '2026-07-28T12:00:00.000Z';
    const conversation = fixtures.data.conversations.find(
      (candidate) => candidate.projectId === projectA.id,
    )!;
    useConversationPreferencesStore
      .getState()
      .selectConversation(profileA.id, projectA.id, conversation.id, selectedAt);

    const selection =
      useConversationPreferencesStore.getState().selectionsByProfileAndProject[profileA.id][
        projectA.id
      ];
    expect(selection.conversationId).toBe(conversation.id);
    expect(Date.parse(selection.validUntil) - Date.parse(selectedAt)).toBe(
      LAST_CONVERSATION_VALIDITY_MS,
    );
    expect(isConversationSelectionCurrent(selection, Date.parse(selectedAt))).toBe(true);
    expect(isConversationSelectionCurrent(selection, Date.parse(selection.validUntil))).toBe(false);
  });
});
