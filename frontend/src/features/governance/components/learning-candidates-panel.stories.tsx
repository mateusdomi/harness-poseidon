import type { Meta, StoryObj } from '@storybook/react';

import { LearningCandidateStateBadge } from './learning-candidates-panel';

const states = [
  'candidate', 'in_review', 'awaiting_evaluation', 'evaluated', 'shadow',
  'approved', 'rejected', 'promoted', 'rolled_back', 'deprecated',
] as const;

const meta = {
  title: 'Governance/Learning candidate state',
  component: LearningCandidateStateBadge,
  tags: ['autodocs'],
  args: { state: 'candidate' },
  argTypes: { state: { control: 'select', options: states } },
} satisfies Meta<typeof LearningCandidateStateBadge>;

export default meta;
type Story = StoryObj<typeof meta>;

export const Default: Story = {};

export const Lifecycle: Story = {
  render: () => <div className="flex flex-wrap gap-2">{states.map((state) => <LearningCandidateStateBadge key={state} state={state} />)}</div>,
};
