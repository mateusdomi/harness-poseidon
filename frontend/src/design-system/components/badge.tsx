import * as React from 'react';
import type { VariantProps } from 'class-variance-authority';

import { cn } from '@/lib/utils';

import { badgeVariants } from './badge-variants';

export interface BadgeProps
  extends React.HTMLAttributes<HTMLSpanElement>,
    VariantProps<typeof badgeVariants> {}

function Badge({ className, variant, ...props }: BadgeProps) {
  return (
    <span
      {...props}
      data-slot="badge"
      className={cn(badgeVariants({ variant }), className)}
    />
  );
}

export { Badge };
