import type { Meta, StoryObj } from '@storybook/react';
import { Gauge } from 'lucide-react';

import { Tooltip } from './tooltip';

const meta = {
  title: 'Design System/Tooltip',
  component: Tooltip,
  tags: ['autodocs'],
} satisfies Meta<typeof Tooltip>;

export default meta;
type Story = StoryObj<typeof meta>;

/** Passe o mouse ou foque pelo teclado para exibir o tooltip à direita. */
export const Default: Story = {
  args: {
    label: 'Cockpit',
    children: (
      <button type="button" aria-label="Cockpit" className="rounded-md p-2 hover:bg-surface-elevated">
        <Gauge aria-hidden="true" className="size-5" />
      </button>
    ),
  },
};
