import { Skeleton } from '@/design-system';

/** Fallback de Suspense para rotas lazy: esqueleto de página. */
export function RouteSkeleton() {
  return (
    <div className="flex flex-col gap-6" role="status" aria-busy="true">
      <Skeleton className="h-8 w-48" />
      <Skeleton className="h-4 w-full max-w-prose" />
      <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
        <Skeleton className="h-24 w-full" />
        <Skeleton className="h-24 w-full" />
        <Skeleton className="h-24 w-full" />
        <Skeleton className="h-24 w-full" />
      </div>
    </div>
  );
}
