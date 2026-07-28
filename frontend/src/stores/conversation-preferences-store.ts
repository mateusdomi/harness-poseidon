import { create } from 'zustand';
import { createJSONStorage, persist } from 'zustand/middleware';

import type { Ulid } from '@/api';

export const LAST_CONVERSATION_VALIDITY_MS = 180 * 24 * 60 * 60 * 1_000;

export interface LastConversationSelection {
  conversationId: Ulid;
  selectedAt: string;
  validUntil: string;
}

type ProfileConversationSelections = Record<Ulid, Record<Ulid, LastConversationSelection>>;

interface ConversationPreferencesState {
  selectionsByProfileAndProject: ProfileConversationSelections;
  selectConversation: (
    profileId: Ulid,
    projectId: Ulid,
    conversationId: Ulid,
    selectedAt?: string,
  ) => void;
  clearConversation: (profileId: Ulid, projectId: Ulid) => void;
}

export function isConversationSelectionCurrent(
  selection: LastConversationSelection | undefined,
  now = Date.now(),
): selection is LastConversationSelection {
  return selection !== undefined && Date.parse(selection.validUntil) > now;
}

export const useConversationPreferencesStore = create<ConversationPreferencesState>()(
  persist(
    (set) => ({
      selectionsByProfileAndProject: {},
      selectConversation: (
        profileId,
        projectId,
        conversationId,
        selectedAt = new Date().toISOString(),
      ) =>
        set((state) => {
          const selectedAtMs = Date.parse(selectedAt);
          const normalizedSelectedAt = Number.isFinite(selectedAtMs)
            ? new Date(selectedAtMs).toISOString()
            : new Date().toISOString();
          return {
            selectionsByProfileAndProject: {
              ...state.selectionsByProfileAndProject,
              [profileId]: {
                ...state.selectionsByProfileAndProject[profileId],
                [projectId]: {
                  conversationId,
                  selectedAt: normalizedSelectedAt,
                  validUntil: new Date(
                    Date.parse(normalizedSelectedAt) + LAST_CONVERSATION_VALIDITY_MS,
                  ).toISOString(),
                },
              },
            },
          };
        }),
      clearConversation: (profileId, projectId) =>
        set((state) => {
          const projectSelections = {
            ...state.selectionsByProfileAndProject[profileId],
          };
          delete projectSelections[projectId];
          return {
            selectionsByProfileAndProject: {
              ...state.selectionsByProfileAndProject,
              [profileId]: projectSelections,
            },
          };
        }),
    }),
    {
      name: 'poseidon-conversation-preferences',
      storage: createJSONStorage(() => localStorage),
      partialize: (state) => ({
        selectionsByProfileAndProject: state.selectionsByProfileAndProject,
      }),
      version: 1,
    },
  ),
);
