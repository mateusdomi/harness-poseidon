import type { Meta, StoryObj } from '@storybook/react';

import { Checkbox } from './checkbox';

const meta = {
  title: 'Design System/Checkbox',
  component: Checkbox,
  tags: ['autodocs'],
  args: { 'aria-label': 'Exemplo' },
} satisfies Meta<typeof Checkbox>;

export default meta;
type Story = StoryObj<typeof meta>;

export const Unchecked: Story = { args: { checked: false, readOnly: true } };

export const Checked: Story = { args: { checked: true, readOnly: true } };

export const Indeterminate: Story = { args: { indeterminate: true, readOnly: true } };

export const DisabledUnchecked: Story = {
  args: { checked: false, disabled: true, readOnly: true },
};

export const DisabledChecked: Story = {
  args: { checked: true, disabled: true, readOnly: true },
};

export const Invalid: Story = {
  args: { checked: false, 'aria-invalid': true, readOnly: true },
};

/** Matriz completa de estados — base para revisão visual em dark/light. */
export const AllStates: Story = {
  render: () => (
    <div className="grid grid-cols-[auto_1fr] items-center gap-x-3 gap-y-3 text-sm text-foreground">
      <Checkbox aria-label="unchecked" checked={false} readOnly />
      <span>Desmarcado</span>
      <Checkbox aria-label="checked" checked readOnly />
      <span>Marcado (preenchimento + check)</span>
      <Checkbox aria-label="indeterminate" indeterminate readOnly />
      <span>Indeterminado (traço)</span>
      <Checkbox aria-label="disabled unchecked" checked={false} disabled readOnly />
      <span>Desabilitado desmarcado</span>
      <Checkbox aria-label="disabled checked" checked disabled readOnly />
      <span>Desabilitado marcado</span>
      <Checkbox aria-label="invalid" checked={false} aria-invalid readOnly />
      <span>Inválido</span>
    </div>
  ),
};

/** Label clicável associado ao controle. */
export const WithLabel: Story = {
  render: () => (
    <label className="flex min-h-11 items-center gap-2 text-sm text-foreground">
      <Checkbox defaultChecked />
      Notificações ativadas
    </label>
  ),
};
