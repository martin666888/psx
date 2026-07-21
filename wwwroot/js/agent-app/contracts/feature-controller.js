// feature-controller.ts — lifecycle contract for a workspace feature domain.
//
// Phase 2 has a single FeatureController implementation (LegacyAgentAdapter).
// Phase 4 introduces one per domain (Session/Inspector/Composer/Decision/
// Timeline). Every controller owns a State slice, a DOM region, its user-input
// listeners, and its own disposables — mount/update/dispose bound that scope.
export {};
