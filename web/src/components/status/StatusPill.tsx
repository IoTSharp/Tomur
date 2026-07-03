export function StatusPill({
  label,
  value,
  tone,
  onClick
}: {
  label: string;
  value: string;
  tone: "default" | "success" | "warning";
  onClick: () => void;
}) {
  return (
    <button className={`status-pill status-pill-${tone}`} type="button" onClick={onClick}>
      <span className="status-pill-label">{label}</span>
      <span className="status-pill-value">{value}</span>
    </button>
  );
}
