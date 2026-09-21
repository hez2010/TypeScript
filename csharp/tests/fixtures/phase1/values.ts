export type Scalar = string | number;
export type Choice = "ready" | "waiting";
export type Nullable = Scalar | undefined | null;
export const value: Scalar = 42;
export const message: Choice = "ready";
