export default function Card({ as: Tag = 'section', className = '', children, ...rest }) {
  return (
    <Tag className={`rounded-ui border border-line bg-surface p-6 shadow-sm ${className}`} {...rest}>
      {children}
    </Tag>
  )
}
